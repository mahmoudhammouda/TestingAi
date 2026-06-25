using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    /// <summary>
    /// Adapte le PROJET SOURCE pour débloquer la compilation du projet de tests lorsque
    /// celle-ci échoue à cause de l'accessibilité de la source — par exemple une classe
    /// 'internal', ou un 'Program' à instructions de haut niveau (top-level statements)
    /// dont le type généré est 'internal' → CS0122 « inaccessible due to protection level ».
    ///
    /// Stratégie DÉTERMINISTE et SANS changement de comportement : on ajoute au projet
    /// source un fichier « ami d'assembly » portant
    /// <c>[assembly: InternalsVisibleTo("&lt;assembly de tests&gt;")]</c>. C'est le pattern
    /// .NET idiomatique pour tester des types 'internal' : il expose ces types au projet de
    /// tests sans toucher ni à la logique ni à l'API publique du code de l'utilisateur.
    /// </summary>
    public class SourceAdapterAgent : BaseAgent
    {
        public override string Name => "SourceAdapter";

        // Nom de fichier dédié et reconnaissable : il est créé par l'adaptateur et supprimé
        // lors d'un retour arrière si l'adaptation ne débloque pas le build.
        public const string ShimFileName = "__TestVisibility.g.cs";

        public SourceAdapterAgent(IDbContext dbContext, ILlmService llmService, ILogger<SourceAdapterAgent> logger)
            : base(dbContext, llmService, logger) { }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            var srcDir = state.SourceProjectPath;
            if (string.IsNullOrWhiteSpace(srcDir) || !Directory.Exists(srcDir))
                throw new Exception($"Projet source introuvable pour l'adaptation : {srcDir}");

            var testAssembly = ResolveTestAssemblyName(state.TestProjectPath);
            var shimPath = Path.Combine(srcDir, ShimFileName);

            var content =
                "// Fichier généré automatiquement par l'agent SourceAdapter.\n" +
                "// Expose les types 'internal' du projet source au projet de tests afin de\n" +
                "// débloquer la compilation des tests (CS0122) — SANS modifier la logique métier.\n" +
                "using System.Runtime.CompilerServices;\n\n" +
                $"[assembly: InternalsVisibleTo(\"{testAssembly}\")]\n";

            await File.WriteAllTextAsync(shimPath, content);
            _logger.LogInformation("[SourceAdapter] InternalsVisibleTo(\"{Asm}\") ajouté à {File}", testAssembly, shimPath);

            await _dbContext.LogCommunicationAsync(
                state.SessionId, Name,
                $"Adaptation de la source : InternalsVisibleTo(\"{testAssembly}\")",
                $"Build des tests bloqué par l'accessibilité de la source (CS0122). " +
                $"Ajout d'un fichier « ami d'assembly » dans le projet source : {srcDir}",
                $"Fichier ajouté : {ShimFileName}\n\n{content}");
        }

        // L'assembly de tests vaut <AssemblyName> du .csproj de tests s'il est défini, sinon
        // le nom du fichier .csproj (convention MSBuild par défaut). Pour les sessions
        // /from-code, il s'agit de « Src.Tests ».
        private static string ResolveTestAssemblyName(string testProjectDir)
        {
            if (!string.IsNullOrWhiteSpace(testProjectDir) && Directory.Exists(testProjectDir))
            {
                var csproj = Directory
                    .EnumerateFiles(testProjectDir, "*.csproj", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (csproj != null)
                {
                    try
                    {
                        var xml = File.ReadAllText(csproj);
                        var m = Regex.Match(xml, @"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>", RegexOptions.IgnoreCase);
                        if (m.Success) return m.Groups[1].Value.Trim();
                    }
                    catch { /* on retombe sur le nom de fichier */ }
                    return Path.GetFileNameWithoutExtension(csproj);
                }
            }
            return "Src.Tests";
        }
    }
}
