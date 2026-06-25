using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Infrastructure.Impl
{
    /// <summary>
    /// Résout les dépendances NuGet d'un projet source soumis par l'utilisateur, par
    /// boucle pilotée par le compilateur : construire le projet seul → lire les espaces
    /// de noms manquants (CS0246) → résoudre en package → réécrire le csproj → recommencer.
    /// </summary>
    public class NuGetPackageResolver : INuGetPackageResolver
    {
        // HttpClient partagé (convention du dépôt — cf. fournisseurs LLM).
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly IProcessRunner _runner;
        private readonly ILogger<NuGetPackageResolver> _logger;

        // Limites de sûreté (anti-boucle, anti-abus réseau / supply-chain).
        private const int MaxIterations = 5;
        private const int MaxPackages = 10;
        private const int BuildTimeoutSeconds = 180;

        // Surcharges curées : espaces de noms dont l'ID de package diverge de l'espace de
        // noms, OU pour lesquels on veut épingler une version connue compatible net8.0.
        // La recherche NuGet générique gère tous les autres cas. Source d'autorité.
        private static readonly (string Namespace, string Package, string Version)[] CuratedOverrides =
        {
            ("System.Threading.Tasks.Dataflow", "System.Threading.Tasks.Dataflow", "8.0.0"),
            ("Newtonsoft.Json",                 "Newtonsoft.Json",                 "13.0.3"),
        };

        public NuGetPackageResolver(IProcessRunner runner, ILogger<NuGetPackageResolver> logger)
        {
            _runner = runner;
            _logger = logger;
        }

        public async Task<SourceResolveResult> EnsureSourceCompilesAsync(string srcDir, CancellationToken ct = default)
        {
            var csprojPath = Path.Combine(srcDir, "Src.csproj");
            var packages = new Dictionary<string, NuGetPackageRef>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new HashSet<string>(StringComparer.Ordinal);

            // Espaces de noms importés par le code source (utiles pour le motif B faible).
            var usingNamespaces = ExtractUsingNamespaces(srcDir);

            // Amorçage : surcharges curées effectivement importées par le code source
            // (évite un premier build voué à l'échec pour les cas connus comme Dataflow).
            foreach (var (ns, pkg, ver) in CuratedOverrides)
            {
                if (usingNamespaces.Any(u => u == ns || u.StartsWith(ns + ".", StringComparison.Ordinal)))
                    packages[pkg] = new NuGetPackageRef(pkg, ver);
            }

            string lastBuildOutput = "";
            for (int iteration = 1; iteration <= MaxIterations; iteration++)
            {
                WriteCsproj(csprojPath, packages.Values);

                var (exitCode, output, error) =
                    await _runner.RunAsync(srcDir, "dotnet", "build Src.csproj --nologo", BuildTimeoutSeconds);
                lastBuildOutput = string.Join("\n",
                    new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));

                if (exitCode == 0)
                {
                    _logger.LogInformation("[NuGetResolver] Code source compilé (itération {N}). Packages : {Pkgs}",
                        iteration, packages.Count == 0 ? "aucun" : string.Join(", ", packages.Keys));
                    return new SourceResolveResult(true, packages.Values.ToList(), unresolved.ToList(), lastBuildOutput);
                }

                var candidates = ExtractMissingNamespaces(lastBuildOutput, usingNamespaces);
                if (candidates.Count == 0)
                {
                    // Échec de build SANS CS0246 exploitable → erreur non liée à un package
                    // (ex. faute de syntaxe dans le code soumis). Inutile de continuer.
                    _logger.LogWarning("[NuGetResolver] Échec de build sans dépendance manquante détectable (itération {N}).", iteration);
                    break;
                }

                bool added = false;
                foreach (var candidate in candidates)
                {
                    if (packages.Count >= MaxPackages) break;
                    var pkg = await ResolvePackageAsync(candidate, ct);
                    if (pkg == null) { unresolved.Add(candidate); continue; }
                    if (packages.ContainsKey(pkg.Id)) continue; // déjà présent → évite la boucle infinie
                    packages[pkg.Id] = pkg;
                    unresolved.Remove(candidate);
                    added = true;
                    _logger.LogInformation("[NuGetResolver] Dépendance résolue : {Ns} → {Pkg} {Ver}", candidate, pkg.Id, pkg.Version);
                }

                if (!added)
                {
                    // Plus aucun progrès possible : les espaces de noms encore signalés par le
                    // compilateur sont déclarés non résolus (package introuvable, version
                    // incompatible net8.0, ou ambiguïté id ≠ namespace non couverte par les
                    // surcharges). On les remonte pour un message d'erreur précis.
                    foreach (var candidate in candidates) unresolved.Add(candidate);
                    _logger.LogWarning("[NuGetResolver] Aucun nouveau package résolu (itération {N}) — arrêt. Non résolus : {Ns}",
                        iteration, string.Join(", ", candidates));
                    break;
                }
            }

            return new SourceResolveResult(false, packages.Values.ToList(), unresolved.ToList(), lastBuildOutput);
        }

        // ── Écriture du csproj : modèle figé + ItemGroup de PackageReference ──────────
        private static void WriteCsproj(string csprojPath, IEnumerable<NuGetPackageRef> packages)
        {
            var refs = packages
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"    <PackageReference Include=\"{p.Id}\" Version=\"{p.Version}\" />");
            var itemGroup = refs.Any()
                ? "\n  <ItemGroup>\n" + string.Join("\n", refs) + "\n  </ItemGroup>"
                : "";
            var xml =
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>{itemGroup}
</Project>";
            File.WriteAllText(csprojPath, xml);
        }

        // ── Espaces de noms importés (using / global using / using static / alias) ────
        private static List<string> ExtractUsingNamespaces(string srcDir)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var rx = new Regex(
                @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?([A-Za-z_][\w.]*)\s*;",
                RegexOptions.Multiline);
            foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                foreach (Match m in rx.Matches(text))
                    result.Add(m.Groups[1].Value);
            }
            return result.ToList();
        }

        // ── Espaces de noms manquants depuis les diagnostics CS0246 ───────────────────
        private static List<string> ExtractMissingNamespaces(string buildOutput, List<string> usingNamespaces)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Motif A (fort) : "name 'X' does not exist in the namespace 'Y'" → Y.X
            var rxA = new Regex(@"CS0246:.*?name '([^']+)' does not exist in the namespace '([^']+)'");
            // Motif B (faible) : "name 'X' could not be found" → on ne déduit QUE depuis
            // un using explicite dont X est la racine (anti-faux-positifs).
            var rxB = new Regex(@"CS0246:.*?name '([^']+)' could not be found");

            foreach (var line in buildOutput.Split('\n'))
            {
                var mA = rxA.Match(line);
                if (mA.Success)
                {
                    var cand = mA.Groups[2].Value + "." + mA.Groups[1].Value;
                    if (seen.Add(cand)) candidates.Add(cand);
                    continue;
                }
                var mB = rxB.Match(line);
                if (mB.Success)
                {
                    var token = mB.Groups[1].Value;
                    var ns = usingNamespaces.FirstOrDefault(u =>
                        u == token || u.StartsWith(token + ".", StringComparison.Ordinal));
                    if (ns != null && seen.Add(ns)) candidates.Add(ns);
                }
            }
            return candidates;
        }

        // ── Résolution espace de noms → package : surcharge curée puis recherche NuGet ─
        private async Task<NuGetPackageRef?> ResolvePackageAsync(string ns, CancellationToken ct)
        {
            // 1. Surcharge curée (autorité ; gère les divergences id ≠ namespace).
            foreach (var (cns, pkg, ver) in CuratedOverrides)
            {
                if (ns == cns || ns.StartsWith(cns + ".", StringComparison.Ordinal))
                    return new NuGetPackageRef(pkg, ver);
            }

            // 2. Recherche NuGet : on tente l'espace de noms comme ID de package puis on
            //    rogne le segment de droite (Foo.Bar.Baz → Foo.Bar → Foo). On n'essaie donc
            //    QUE des ID qui sont des ancêtres de l'espace de noms réellement référencé
            //    par le code (garde-fou anti-typosquat).
            var parts = ns.Split('.');
            for (int len = parts.Length; len >= 1; len--)
            {
                var candidateId = string.Join('.', parts.Take(len));
                var version = await GetLatestStableVersionAsync(candidateId, ct);
                if (version != null)
                    return new NuGetPackageRef(candidateId, version);
            }
            return null;
        }

        // ── Dernière version STABLE d'un package via le flat-container NuGet ───────────
        private async Task<string?> GetLatestStableVersionAsync(string packageId, CancellationToken ct)
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null; // 404 → ce package n'existe pas
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("versions", out var versions)) return null;

                string? latestStable = null; // la liste flat-container est triée en ordre SemVer croissant
                foreach (var v in versions.EnumerateArray())
                {
                    var ver = v.GetString();
                    if (string.IsNullOrEmpty(ver) || ver.Contains('-')) continue; // ignore les préversions
                    latestStable = ver;
                }
                return latestStable;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NuGetResolver] Requête NuGet échouée pour {Id}", packageId);
                return null;
            }
        }
    }
}
