using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Impl.Services;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;
using TestingAi.Agents.Infrastructure.Impl;

var builder = WebApplication.CreateBuilder(args);

// ── Logging : log4net ─────────────────────────────────────────────────────────
// API  → Logs/api.log     (logger racine)
// Agents → Logs/agents.log  (logger TestingAi.Agents.*)
builder.Logging.ClearProviders();
builder.Logging.AddLog4Net("log4net.config");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var config = builder.Configuration;
var dbPath = config["DbPath"] ?? "Data/TestingAi.Agents.db";

// Répertoire de travail PERSISTANT des sessions, sous Data/ (à côté de la base).
// Chaque session y possède son dossier <guid>/{Src, Src.Tests} contenant le code
// source de travail et les tests générés. Remplace l'ancien /tmp (purgé par le
// système) → les fichiers de travail survivent désormais aux redémarrages.
// Chemin ABSOLU : stocké tel quel dans SourceProject/TargetProject puis réutilisé par
// les agents (ex. TestRunner lance `dotnet test "<chemin>"` AVEC le projet en argument
// ET comme répertoire de travail — un chemin relatif serait appliqué deux fois → MSB1009).
var sessionsRoot = Path.GetFullPath(config["SessionsRoot"]
    ?? Path.Combine(Path.GetDirectoryName(dbPath) is { Length: > 0 } dataDir ? dataDir : "Data", "Sessions"));
Directory.CreateDirectory(sessionsRoot);

builder.Services.AddSingleton<IDbContext>(sp => new DbContext(dbPath));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<INuGetPackageResolver, NuGetPackageResolver>();
builder.Services.AddSingleton<ILlmProvider>(sp =>
    new GeminiLlmProvider(config["GeminiApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")));
builder.Services.AddSingleton<ILlmProvider>(sp =>
    new OpenAiLlmProvider(config["OpenAiApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")));
builder.Services.AddSingleton<ILlmProvider>(sp =>
    new AnthropicLlmProvider(config["AnthropicApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")));
builder.Services.AddSingleton<ILlmService, LlmService>();
// Agents de GÉNÉRATION (phase 0)
builder.Services.AddSingleton<AnalyzerAgent>();
builder.Services.AddSingleton<DeciderAgent>();
builder.Services.AddSingleton<CreatorAgent>();
builder.Services.AddSingleton<ExporterAgent>();
builder.Services.AddSingleton<VerifierAgent>();
builder.Services.AddSingleton<ReviewerAgent>();
builder.Services.AddSingleton<SourceAdapterAgent>();
// Agents de VÉRIFICATION (phases 1-3)
builder.Services.AddSingleton<TestDiscoveryAgent>();
builder.Services.AddSingleton<TestRunnerAgent>();
builder.Services.AddSingleton<TestDecisionAgent>();
builder.Services.AddSingleton<TestFixerAgent>();
builder.Services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

var app = builder.Build();

app.UseCors();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

var db = app.Services.GetRequiredService<IDbContext>();
await db.InitializeAsync();

// ── SESSIONS ──────────────────────────────────────────────────────────────────

app.MapGet("/api/sessions", async (IDbContext dbCtx) =>
{
    var sessions = await dbCtx.GetAllSessionsAsync();
    var tasks = sessions.Select(async s =>
    {
        var tests = (await dbCtx.GetTestCasesAsync(s.Id)).ToList();
        return new
        {
            s.Id, s.TargetProject, s.SourceProject, s.Status, s.CreatedAt,
            Summary = new
            {
                Total = tests.Count,
                Green = tests.Count(t => t.Status == TestStatus.Green),
                Red = tests.Count(t => t.Status == TestStatus.Red),
                Pending = tests.Count(t => t.Status == TestStatus.Pending),
                AwaitingDecision = tests.Count(t => t.Status == TestStatus.EnAttenteDecision),
                Ignored = tests.Count(t => t.Status == TestStatus.Ignored)
            }
        };
    });
    return Results.Ok(await Task.WhenAll(tasks));
}).WithName("GetSessions").WithOpenApi();

app.MapPost("/api/sessions/from-code", async (StartSessionFromCodeRequest req, IAgentOrchestrator orch, INuGetPackageResolver nugetResolver, ILogger<Program> log) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.SourceCode))
            return Results.BadRequest(new { detail = "Le code source ne peut pas être vide." });

        var rawName = (req.FileName?.Trim() is { Length: > 0 } fn ? fn : "MyCode");
        // Empêche la traversée de chemin : ne conserve que le nom de fichier (retire tout séparateur / "..").
        var safeName = Path.GetFileName(rawName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "MyCode";
        var fileName = safeName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? safeName : safeName + ".cs";

        var sessionDir = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));

        // ── Projet source ────────────────────────────────────────────────────
        var srcDir = Path.Combine(sessionDir, "Src");
        Directory.CreateDirectory(srcDir);
        // Nettoyage : retire les using xUnit/NUnit et les blocs namespace *.Tests
        // au cas où l'utilisateur colle un fichier mixte (source + tests)
        await File.WriteAllTextAsync(Path.Combine(srcDir, fileName), SourceCodeHelper.CleanSourceCode(req.SourceCode));

        // ── Résolution des dépendances du code source ─────────────────────────
        // Certains espaces de noms ne font pas partie du framework partagé .NET 8
        // (ex. TPL Dataflow) et nécessitent une référence NuGet. On construit le projet
        // source SEUL et on injecte les <PackageReference> manquantes (liste curée +
        // recherche NuGet) AVANT la génération de tests : sinon le code source ne compile
        // pas et toute la chaîne échoue. Le résolveur écrit lui-même Src.csproj.
        var resolveResult = await nugetResolver.EnsureSourceCompilesAsync(srcDir);
        if (!resolveResult.Compiles)
        {
            log.LogWarning("Le code source soumis ne compile pas. Espaces de noms non résolus : {Ns}",
                resolveResult.Unresolved.Count > 0 ? string.Join(", ", resolveResult.Unresolved) : "(aucun)");
            var detail = resolveResult.Unresolved.Count > 0
                ? $"Le code source ne compile pas : impossible de résoudre la ou les dépendances pour {string.Join(", ", resolveResult.Unresolved)}. " +
                  "Vérifiez le nom des espaces de noms, ou le package n'existe pas sur NuGet."
                : "Le code source ne compile pas (erreur de compilation non liée à un package manquant — ex. syntaxe).";
            var output = resolveResult.BuildOutput.Length > 1500
                ? resolveResult.BuildOutput[..1500] + "…"
                : resolveResult.BuildOutput;
            return Results.Problem(detail + "\n\n" + output);
        }
        if (resolveResult.Resolved.Count > 0)
            log.LogInformation("Packages NuGet injectés dans le projet source : {Pkgs}",
                string.Join(", ", resolveResult.Resolved.Select(p => $"{p.Id} {p.Version}")));

        // ── Projet de tests ──────────────────────────────────────────────────
        var testsDir = Path.Combine(sessionDir, "Src.Tests");
        Directory.CreateDirectory(testsDir);
        await File.WriteAllTextAsync(Path.Combine(testsDir, "Src.Tests.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.0" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Src/Src.csproj" />
              </ItemGroup>
            </Project>
            """);

        // Le fichier de test sera GÉNÉRÉ par la chaîne d'agents
        // (Analyzer→Decider→Creator→Exporter→Verifier), pas écrit ici.
        var sourceFilePath = Path.Combine(srcDir, fileName);

        log.LogInformation("Session from-code : répertoire de travail {Dir}", sessionDir);
        var state = await orch.RunPipelineAsync(testsDir, srcDir, sourceFilePath);
        return Results.Ok(new
        {
            state.SessionId, state.PipelineStatus, state.IsFinished,
            TestCount = state.TestCases.Count,
            Green = state.TestCases.Count(t => t.Status == TestStatus.Green),
            Red = state.TestCases.Count(t => t.Status == TestStatus.Red),
            AwaitingDecision = state.TestCases.Count(t => t.Status == TestStatus.EnAttenteDecision)
        });
    }
    catch (Exception ex) { log.LogError(ex, "Erreur session from-code"); return Results.Problem(ex.Message); }
}).WithName("StartSessionFromCode").WithOpenApi();

// ── Import d'un DOSSIER (webkitdirectory) ─────────────────────────────────────
// Reçoit l'arborescence de fichiers sélectionnée dans le navigateur puis génère des
// tests. Deux cas gérés en Phase 1 :
//   CAS 1 — dossier de .cs SANS csproj/sln → un projet source est synthétisé (résolution NuGet).
//   CAS 2 — dossier avec UN SEUL .csproj    → sa configuration est respectée (build de contrôle).
// Les solutions (.sln) ou projets multiples (>1 .csproj) sont refusés proprement (Phase 2).
// La génération s'exécute EN ARRIÈRE-PLAN (fan-out multi-fichiers) ; l'appel renvoie
// immédiatement l'identifiant de session, que le client sonde ensuite.
app.MapPost("/api/sessions/from-folder", async (
    StartSessionFromFolderRequest req,
    IAgentOrchestrator orch,
    INuGetPackageResolver nugetResolver,
    IProcessRunner procRunner,
    IDbContext dbCtx,
    ILogger<Program> log) =>
{
    try
    {
        var files = req.Files ?? Array.Empty<FolderFile>();
        if (files.Length == 0)
            return Results.BadRequest(new { detail = "Aucun fichier reçu." });

        const int maxFiles = 50;
        const int maxTotalBytes = 5 * 1024 * 1024;
        if (files.Length > maxFiles)
            return Results.BadRequest(new { detail = $"Trop de fichiers ({files.Length}). Maximum autorisé : {maxFiles}." });
        long totalBytes = files.Sum(f => (long)System.Text.Encoding.UTF8.GetByteCount(f.Content ?? string.Empty));
        if (totalBytes > maxTotalBytes)
            return Results.BadRequest(new { detail = $"Contenu trop volumineux ({totalBytes / 1024} Ko). Maximum autorisé : {maxTotalBytes / 1024} Ko." });

        var sessionDir = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(sessionDir, "Src");
        Directory.CreateDirectory(srcDir);

        // Reconstruit l'arborescence sous Src/ en neutralisant toute traversée de chemin.
        int csCount = 0;
        foreach (var f in files)
        {
            if (!FolderImportHelper.TryResolveSafePath(srcDir, f.RelativePath, out var dest))
                return Results.BadRequest(new { detail = $"Chemin de fichier invalide : {f.RelativePath}" });
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllTextAsync(dest, f.Content ?? string.Empty);
            if (dest.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) csCount++;
        }
        if (csCount == 0)
            return Results.BadRequest(new { detail = "Le dossier ne contient aucun fichier .cs à tester." });

        var slnFiles = Directory.EnumerateFiles(srcDir, "*.sln", SearchOption.AllDirectories).ToList();
        var csprojFiles = Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories).ToList();

        if (slnFiles.Count > 0 || csprojFiles.Count > 1)
            return Results.BadRequest(new
            {
                detail =
                    "Ce dossier contient une solution (.sln) ou plusieurs projets (.csproj). " +
                    "La prise en charge des solutions complètes et des projets multiples arrivera dans une prochaine étape. " +
                    "Pour l'instant, importez un dossier de fichiers .cs, ou un dossier contenant un unique projet .csproj."
            });

        string projRefRelative;
        var testsDir = Path.Combine(sessionDir, "Src.Tests");

        if (csprojFiles.Count == 0)
        {
            // ── CAS 1 : bag de .cs → synthèse d'un projet + résolution NuGet ──
            // Le résolveur construit le projet source SEUL, injecte les <PackageReference>
            // manquantes et écrit lui-même Src.csproj. Si ça ne compile pas, on s'arrête ici.
            var resolveResult = await nugetResolver.EnsureSourceCompilesAsync(srcDir);
            if (!resolveResult.Compiles)
            {
                var detail = resolveResult.Unresolved.Count > 0
                    ? $"Le code source ne compile pas : dépendance(s) non résolue(s) pour {string.Join(", ", resolveResult.Unresolved)}. " +
                      "Vérifiez les espaces de noms, ou le package n'existe pas sur NuGet."
                    : "Le code source ne compile pas (erreur de compilation non liée à un package manquant — ex. syntaxe).";
                var output = resolveResult.BuildOutput.Length > 1500 ? resolveResult.BuildOutput[..1500] + "…" : resolveResult.BuildOutput;
                return Results.Problem(detail + "\n\n" + output);
            }
            projRefRelative = "../Src/Src.csproj";
        }
        else
        {
            // ── CAS 2 : projet .csproj unique → on respecte sa configuration ──
            var csprojPath = csprojFiles[0];
            var csprojContent = await File.ReadAllTextAsync(csprojPath);
            var validation = ProjectFileHelper.ResolveBuildTarget(csprojContent, out var chosenTfm);
            if (validation != null)
                return Results.BadRequest(new { detail = validation });

            // Multi-ciblage : on réécrit NOTRE copie de travail du .csproj en mono-cible sur le
            // framework retenu. Indispensable car la restauration évalue TOUS les <TargetFrameworks>
            // et un TFM non gérable ici présent dans la liste (ex. net9.0) ferait échouer tout le
            // build (NETSDK1045), même avec -f. Sans effet si le projet est déjà mono-cible.
            var effectiveCsproj = ProjectFileHelper.RewriteToSingleTfm(csprojContent, chosenTfm);
            if (!string.Equals(effectiveCsproj, csprojContent, StringComparison.Ordinal))
                await File.WriteAllTextAsync(csprojPath, effectiveCsproj);

            // Build de contrôle du projet fourni (le résolveur NuGet N'EST PAS appelé : il
            // écraserait la configuration de l'utilisateur).
            var (exit, output, error) = await procRunner.RunAsync(
                Path.GetDirectoryName(csprojPath)!, "dotnet", $"build \"{csprojPath}\" --nologo", 180);
            if (exit != 0)
            {
                var combined = string.Join("\n", new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (combined.Length > 1500) combined = combined[..1500] + "…";
                return Results.Problem("Le projet fourni ne compile pas dans cet environnement.\n\n" + combined);
            }
            projRefRelative = Path.GetRelativePath(testsDir, csprojPath).Replace('\\', '/');
        }

        // ── Projet de tests ──────────────────────────────────────────────────
        Directory.CreateDirectory(testsDir);
        await File.WriteAllTextAsync(Path.Combine(testsDir, "Src.Tests.csproj"),
            FolderImportHelper.BuildTestCsproj(projRefRelative));

        // Fichiers à couvrir : sélection de l'utilisateur (generateFor) ou tous les .cs.
        var sep = Path.DirectorySeparatorChar;
        var allCs = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .ToList();
        List<string> genFiles;
        if (req.GenerateFor is { Length: > 0 })
        {
            var wanted = new HashSet<string>(
                req.GenerateFor.Select(p => Path.GetFullPath(Path.Combine(srcDir, p.Replace('\\', '/').TrimStart('/')))),
                StringComparer.Ordinal);
            genFiles = allCs.Where(f => wanted.Contains(Path.GetFullPath(f))).ToList();
            if (genFiles.Count == 0) genFiles = allCs;
        }
        else genFiles = allCs;
        genFiles = genFiles.OrderBy(f => f, StringComparer.Ordinal).ToList();

        // ── Création de la session + démarrage en arrière-plan ────────────────
        var sessionId = await dbCtx.CreateSessionAsync(testsDir, srcDir);
        var seedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Total = 0, Green = 0, Red = 0, Ignored = 0, AwaitingDecision = 0,
            Error = "", FilesTotal = genFiles.Count, FilesDone = 0, CurrentFile = ""
        });
        await dbCtx.UpdateSessionStateAsync(sessionId, seedJson, "En_Cours");

        var filesToRun = genFiles;
        _ = Task.Run(async () =>
        {
            try
            {
                await orch.RunPipelineForSessionAsync(sessionId, testsDir, srcDir, filesToRun);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Erreur pipeline dossier (session {Id})", sessionId);
                try
                {
                    var errJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Total = 0, Green = 0, Red = 0, Ignored = 0, AwaitingDecision = 0,
                        Error = ex.Message, FilesTotal = filesToRun.Count, FilesDone = 0, CurrentFile = ""
                    });
                    await dbCtx.UpdateSessionStateAsync(sessionId, errJson, "Erreur");
                }
                catch { /* filet de sécurité */ }
            }
        });

        log.LogInformation("Session from-folder {Id} démarrée : {N} fichier(s) à couvrir, dossier {Dir}",
            sessionId, filesToRun.Count, sessionDir);
        return Results.Ok(new { sessionId, filesTotal = filesToRun.Count });
    }
    catch (Exception ex) { log.LogError(ex, "Erreur session from-folder"); return Results.Problem(ex.Message); }
}).WithName("StartSessionFromFolder").WithOpenApi();

app.MapPost("/api/sessions", async (StartSessionRequest req, IAgentOrchestrator orch, ILogger<Program> log) =>
{
    try
    {
        var state = await orch.RunPipelineAsync(req.TestProjectPath, req.SourceProjectPath ?? "");
        return Results.Ok(new
        {
            state.SessionId, state.PipelineStatus, state.IsFinished,
            TestCount = state.TestCases.Count,
            Green = state.TestCases.Count(t => t.Status == TestStatus.Green),
            Red = state.TestCases.Count(t => t.Status == TestStatus.Red),
            AwaitingDecision = state.TestCases.Count(t => t.Status == TestStatus.EnAttenteDecision)
        });
    }
    catch (Exception ex) { log.LogError(ex, "Erreur session"); return Results.Problem(ex.Message); }
}).WithName("StartSession").WithOpenApi();

app.MapGet("/api/sessions/{id:int}", async (int id, IDbContext dbCtx) =>
{
    var session = await dbCtx.GetSessionAsync(id);
    if (session == null) return Results.NotFound();
    var tests = (await dbCtx.GetTestCasesAsync(id)).ToList();
    return Results.Ok(new
    {
        session.Id, session.TargetProject, session.SourceProject,
        session.Status, session.GlobalState, session.CreatedAt,
        session.Metadata, session.TestStrategy,
        Summary = new
        {
            Total = tests.Count,
            Green = tests.Count(t => t.Status == TestStatus.Green),
            Red = tests.Count(t => t.Status == TestStatus.Red),
            Pending = tests.Count(t => t.Status == TestStatus.Pending),
            AwaitingDecision = tests.Count(t => t.Status == TestStatus.EnAttenteDecision),
            Ignored = tests.Count(t => t.Status == TestStatus.Ignored)
        }
    });
}).WithName("GetSession").WithOpenApi();

app.MapPost("/api/sessions/{id:int}/resume", async (int id, IAgentOrchestrator orch, ILogger<Program> log) =>
{
    try
    {
        var state = await orch.ResumePipelineAsync(id);
        return Results.Ok(new
        {
            state.SessionId, state.PipelineStatus, state.IsFinished,
            Green = state.TestCases.Count(t => t.Status == TestStatus.Green),
            Red = state.TestCases.Count(t => t.Status == TestStatus.Red)
        });
    }
    catch (Exception ex) { log.LogError(ex, "Erreur reprise session {Id}", id); return Results.Problem(ex.Message); }
}).WithName("ResumeSession").WithOpenApi();

app.MapPost("/api/sessions/{id:int}/rerun", async (int id, IAgentOrchestrator orch, ILogger<Program> log) =>
{
    try
    {
        var state = await orch.RerunPipelineAsync(id);
        return Results.Ok(new
        {
            state.SessionId, state.PipelineStatus, state.IsFinished,
            TestCount = state.TestCases.Count,
            Green = state.TestCases.Count(t => t.Status == TestStatus.Green),
            Red = state.TestCases.Count(t => t.Status == TestStatus.Red)
        });
    }
    catch (Exception ex) { log.LogError(ex, "Erreur relance session {Id}", id); return Results.Problem(ex.Message); }
}).WithName("RerunSession").WithOpenApi();

// ── TESTS ─────────────────────────────────────────────────────────────────────

app.MapGet("/api/sessions/{id:int}/tests", async (int id, IDbContext dbCtx) =>
{
    var tests = await dbCtx.GetTestCasesAsync(id);
    return Results.Ok(tests.Select(t => new
    {
        t.Id, t.SessionId, t.TestName, t.ClassName, t.MethodName,
        t.TestFilePath, t.SourceFilePath,
        Status = t.Status.ToString(), Action = t.Action.ToString(),
        t.ErrorMessage, t.FixApplied, t.RetryCount, t.CreatedAt, t.UpdatedAt
    }));
}).WithName("GetTestCases").WithOpenApi();

app.MapGet("/api/sessions/{id:int}/code", async (int id, IDbContext dbCtx) =>
{
    var session = await dbCtx.GetSessionAsync(id);
    if (session == null) return Results.NotFound();

    // 1) On lit le code directement dans le dossier de travail de la session sur le disque
    //    (état le plus à jour, notamment pendant une exécution en cours).
    var (json, hasAny) = CodeSnapshotHelper.CaptureJson(session.SourceProject, session.TargetProject);
    if (hasAny) return Results.Content(json, "application/json");

    // 2) Repli : le dossier de travail a disparu → on sert l'instantané persisté en base
    //    (capturé pendant le pipeline) pour que l'onglet « Code » reste consultable.
    if (!string.IsNullOrEmpty(session.CodeSnapshot))
        return Results.Content(session.CodeSnapshot, "application/json");

    // 3) Aucune source disponible (session antérieure à l'instantané : code définitivement perdu).
    return Results.Content("{\"sourceFiles\":[],\"testFiles\":[]}", "application/json");
}).WithName("GetSessionCode").WithOpenApi();

app.MapGet("/api/tests/{id:int}", async (int id, IDbContext dbCtx) =>
{
    var t = await dbCtx.GetTestCaseAsync(id);
    if (t == null) return Results.NotFound();
    return Results.Ok(new
    {
        t.Id, t.SessionId, t.TestName, t.ClassName, t.MethodName,
        t.TestFilePath, t.SourceFilePath,
        Status = t.Status.ToString(), Action = t.Action.ToString(),
        t.ErrorMessage, t.FixApplied, t.RetryCount, t.CreatedAt, t.UpdatedAt
    });
}).WithName("GetTestCase").WithOpenApi();

app.MapPut("/api/tests/{id:int}/action", async (int id, SetActionRequest req, IDbContext dbCtx) =>
{
    var t = await dbCtx.GetTestCaseAsync(id);
    if (t == null) return Results.NotFound();
    if (!Enum.TryParse<TestAction>(req.Action, true, out var action))
        return Results.BadRequest(new { error = $"Action invalide : {req.Action}" });
    if (action == TestAction.Ignore)
        await dbCtx.UpdateTestCaseStatusAsync(id, TestStatus.Ignored);
    else
        await dbCtx.UpdateTestCaseStatusAsync(id, TestStatus.Red);
    await dbCtx.UpdateTestCaseActionAsync(id, action);
    return Results.Ok(new { id, action = action.ToString() });
}).WithName("SetTestAction").WithOpenApi();

app.MapPut("/api/tests/bulk-action", async (BulkActionRequest req, IDbContext dbCtx) =>
{
    if (!Enum.TryParse<TestAction>(req.Action, true, out var action))
        return Results.BadRequest(new { error = $"Action invalide : {req.Action}" });
    foreach (var id in req.TestIds)
    {
        if (await dbCtx.GetTestCaseAsync(id) == null) continue;
        await dbCtx.UpdateTestCaseStatusAsync(id, action == TestAction.Ignore ? TestStatus.Ignored : TestStatus.Red);
        await dbCtx.UpdateTestCaseActionAsync(id, action);
    }
    return Results.Ok(new { updated = req.TestIds.Length, action = action.ToString() });
}).WithName("BulkSetAction").WithOpenApi();

// ── PARAMÈTRES ────────────────────────────────────────────────────────────────

app.MapGet("/api/settings", async (IDbContext dbCtx) =>
{
    var s = await dbCtx.GetSettingsAsync();
    return Results.Ok(new { s.HumanInterventionEnabled, s.MaxRetries, s.PreferredProvider });
}).WithName("GetSettings").WithOpenApi();

app.MapPut("/api/settings", async (UpdateSettingsRequest req, IDbContext dbCtx, IEnumerable<ILlmProvider> providers) =>
{
    var requested = req.PreferredProvider ?? "Gemini";
    if (!Enum.TryParse<LlmProviderType>(requested, true, out var providerType))
        return Results.BadRequest(new { error = $"Provider inconnu : '{requested}'." });
    if (!providers.Any(p => p.ProviderType == providerType))
    {
        var available = string.Join(", ", providers.Select(p => p.ProviderType));
        return Results.BadRequest(new { error = $"Provider '{providerType}' non disponible. Providers enregistrés : {available}." });
    }

    var s = new PipelineSettings
    {
        HumanInterventionEnabled = req.HumanInterventionEnabled,
        MaxRetries = req.MaxRetries,
        PreferredProvider = providerType.ToString()
    };
    await dbCtx.UpdateSettingsAsync(s);
    return Results.Ok(new { message = "Paramètres mis à jour.", s.HumanInterventionEnabled, s.MaxRetries, s.PreferredProvider });
}).WithName("UpdateSettings").WithOpenApi();

// ── LOGS ──────────────────────────────────────────────────────────────────────

app.MapGet("/api/sessions/{id:int}/logs", async (int id, IDbContext dbCtx) =>
    Results.Ok(await dbCtx.GetCommunicationsAsync(id)))
    .WithName("GetSessionLogs").WithOpenApi();

app.MapGet("/api/sessions/{id:int}/conversation", async (int id, IDbContext dbCtx) =>
    Results.Ok(await dbCtx.GetPrivateMemoryAsync(id)))
    .WithName("GetSessionConversation").WithOpenApi();

app.Run();

record StartSessionRequest(string TestProjectPath, string? SourceProjectPath);
record StartSessionFromCodeRequest(string SourceCode, string? FileName);
record FolderFile(string RelativePath, string Content);
record StartSessionFromFolderRequest(FolderFile[] Files, string[]? GenerateFor);
record SetActionRequest(string Action);
record BulkActionRequest(int[] TestIds, string Action);
record UpdateSettingsRequest(bool HumanInterventionEnabled, int MaxRetries, string? PreferredProvider);

// Nécessaire pour WebApplicationFactory dans les tests d'intégration
public partial class Program { }

// ── Helper : nettoyage du code source soumis par l'utilisateur ────────────────
static class SourceCodeHelper
{
    /// <summary>
    /// Retire du code source les éléments liés aux frameworks de test (using Xunit/NUnit,
    /// blocs namespace *.Tests) afin que le projet Src.csproj (sans référence xUnit) compile.
    /// </summary>
    public static string CleanSourceCode(string code)
    {
        var lines = code.Split('\n');
        var result = new System.Text.StringBuilder();
        int skipDepth = 0;   // profondeur d'accolades à ignorer (bloc namespace Tests)
        bool inSkipBlock = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimStart();

            // 1. Supprimer les using de frameworks de test
            if (!inSkipBlock && System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                    @"^using\s+(Xunit|NUnit|Microsoft\.VisualStudio\.TestTools|NUnit\.Framework)\b"))
                continue;

            // 2. Détecter le début d'un namespace de test
            if (!inSkipBlock && System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                    @"^namespace\s+\S*\.(Tests?|Specs?)\b"))
            {
                inSkipBlock = true;
                skipDepth = 0;
            }

            if (inSkipBlock)
            {
                foreach (var ch in rawLine)
                {
                    if (ch == '{') skipDepth++;
                    else if (ch == '}') skipDepth--;
                }
                // Quand on revient à la profondeur 0 après avoir ouvert au moins une accolade
                if (skipDepth <= 0 && rawLine.Contains('}'))
                    inSkipBlock = false;
                continue;
            }

            result.AppendLine(rawLine);
        }

        return result.ToString().Trim();
    }
}

// ── Helper : import de dossier (reconstruction d'arborescence + projet de tests) ──
static class FolderImportHelper
{
    /// <summary>
    /// Résout un chemin relatif SOUS baseDir en neutralisant toute traversée de chemin
    /// (chemins absolus, segments « .. »). Renvoie false si le chemin s'échappe de baseDir.
    /// </summary>
    public static bool TryResolveSafePath(string baseDir, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0) return false;
        foreach (var seg in normalized.Split('/'))
            if (seg == "..") return false;

        var baseFull = Path.GetFullPath(baseDir);
        var combined = Path.GetFullPath(Path.Combine(baseFull, normalized));
        if (combined != baseFull &&
            !combined.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        fullPath = combined;
        return true;
    }

    /// <summary>Génère le contenu du projet xUnit (Src.Tests.csproj) référençant le projet source.</summary>
    public static string BuildTestCsproj(string projectReferenceRelativePath) =>
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
            <PackageReference Include="xunit" Version="2.9.0" />
            <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
              <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
              <PrivateAssets>all</PrivateAssets>
            </PackageReference>
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="{{projectReferenceRelativePath}}" />
          </ItemGroup>
        </Project>
        """;
}

// ── Helper : détection du framework cible d'un .csproj fourni (CAS 2) ────────────
public static class ProjectFileHelper
{
    // Frameworks pris en charge, listés dans le message d'erreur agrégé.
    const string SupportedList =
        "netstandard2.0/2.1, netcoreapp3.0/3.1, net5.0 à net8.0";

    /// <summary>
    /// Détecte le(s) framework(s) cible(s) déclaré(s) (mono ou multi-ciblage) et sélectionne
    /// celui à compiler dans cet environnement (SDK .NET 8, Linux). Renvoie null si un TFM
    /// exploitable a été trouvé (<paramref name="chosenTfm"/> renseigné), sinon un message
    /// d'erreur (français) expliquant pourquoi aucun n'est pris en charge.
    /// </summary>
    public static string? ResolveBuildTarget(string csprojContent, out string chosenTfm)
    {
        chosenTfm = string.Empty;

        // WPF / WinForms : dépendances Windows uniquement.
        if (Regex.IsMatch(csprojContent, @"<UseWPF>\s*true", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(csprojContent, @"<UseWindowsForms>\s*true", RegexOptions.IgnoreCase))
            return "Les projets WPF / WinForms ne sont pas pris en charge dans cet environnement Linux.";

        var tfms = ParseTargetFrameworks(csprojContent);
        if (tfms.Count == 0)
            return "Impossible de déterminer le(s) framework(s) cible(s) (<TargetFramework> / <TargetFrameworks>) du projet.";

        // Ne garder que les TFM compilables ici ET consommables par un projet de tests net8.0.
        var supported = tfms
            .Select(t => (Tfm: t, Score: ScoreIfSupported(t)))
            .Where(x => x.Score > 0)
            .ToList();

        if (supported.Count == 0)
        {
            var reasons = string.Join(" ; ", tfms.Select(UnsupportedReason));
            return $"Aucun framework cible pris en charge : {reasons}. " +
                   $"Frameworks pris en charge dans cet environnement : {SupportedList}.";
        }

        // Choisir le TFM le plus proche de net8.0 (meilleur score) → build de contrôle le
        // plus fiable, et correspond au TFM que MSBuild retiendra pour la ProjectReference.
        chosenTfm = supported.OrderByDescending(x => x.Score).First().Tfm;
        return null;
    }

    /// <summary>
    /// Réécrit un &lt;TargetFrameworks&gt; (multi-ciblage) en &lt;TargetFramework&gt; mono-cible sur le
    /// TFM retenu, pour que la restauration/compilation n'évalue QUE ce framework. Sans effet
    /// si le projet est déjà mono-cible (aucun &lt;TargetFrameworks&gt;).
    /// </summary>
    public static string RewriteToSingleTfm(string csprojContent, string chosenTfm) =>
        Regex.Replace(
            csprojContent,
            @"<TargetFrameworks>\s*[^<]+?\s*</TargetFrameworks>",
            $"<TargetFramework>{chosenTfm}</TargetFramework>",
            RegexOptions.IgnoreCase);

    /// <summary>Extrait la liste des TFM depuis &lt;TargetFrameworks&gt; (pluriel) ou &lt;TargetFramework&gt;.</summary>
    public static List<string> ParseTargetFrameworks(string csprojContent)
    {
        var result = new List<string>();
        var multi = Regex.Match(csprojContent, @"<TargetFrameworks>\s*([^<]+?)\s*</TargetFrameworks>", RegexOptions.IgnoreCase);
        if (multi.Success)
        {
            foreach (var t in multi.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                result.Add(t);
        }
        else
        {
            var single = Regex.Match(csprojContent, @"<TargetFramework>\s*([^<]+?)\s*</TargetFramework>", RegexOptions.IgnoreCase);
            if (single.Success)
                result.Add(single.Groups[1].Value.Trim());
        }
        return result;
    }

    /// <summary>
    /// Renvoie un score &gt; 0 si le TFM est compilable ici ET consommable par un projet de
    /// tests net8.0 ; 0 sinon. Score plus élevé = plus proche de net8.0 (préféré).
    /// </summary>
    static int ScoreIfSupported(string rawTfm)
    {
        var t = rawTfm.Trim().ToLowerInvariant();
        if (t.Length == 0) return 0;

        // Cibles spécifiques à un OS (net8.0-windows, net6.0-android, …) : workloads/OS absents.
        if (t.Contains('-')) return 0;

        // netstandard*X.Y : consommable par net8.0 ; 2.1 > 2.0 > (1.x best-effort).
        // Regex stricte : une valeur « netstandard… » malformée (ex. netstandard$(Prop))
        // ne doit PAS être retenue puis échouer au build avec une erreur SDK brute.
        if (Regex.IsMatch(t, @"^netstandard\d+\.\d+$"))
            return t switch { "netstandard2.1" => 50, "netstandard2.0" => 45, _ => 35 };

        // netcoreappX.Y
        var nca = Regex.Match(t, @"^netcoreapp(\d+)\.(\d+)$");
        if (nca.Success)
        {
            int maj = int.Parse(nca.Groups[1].Value), min = int.Parse(nca.Groups[2].Value);
            if (maj == 3) return min >= 1 ? 60 : 55;   // netcoreapp3.1 / 3.0
            if (maj == 2) return 40;                    // netcoreapp2.x (best-effort)
            return 30;                                  // 1.x (best-effort)
        }

        // netX.Y moderne (avec point) : net5.0..net8.0 ok ; net9.0+ = SDK trop récent.
        var mod = Regex.Match(t, @"^net(\d+)\.(\d+)$");
        if (mod.Success)
        {
            int maj = int.Parse(mod.Groups[1].Value);
            return maj switch { 8 => 100, 7 => 90, 6 => 80, 5 => 70, _ => 0 };
        }

        // .NET Framework (net48, net472, net40, …) : sans point → Windows/mono. Ou inconnu.
        return 0;
    }

    /// <summary>Explique en français pourquoi un TFM n'est pas exploitable ici.</summary>
    static string UnsupportedReason(string rawTfm)
    {
        var t = rawTfm.Trim().ToLowerInvariant();
        if (t.Contains('-'))
            return $"« {rawTfm} » cible une plateforme spécifique (workload/OS absent sur ce serveur Linux)";
        var mod = Regex.Match(t, @"^net(\d+)\.(\d+)$");
        if (mod.Success && int.Parse(mod.Groups[1].Value) >= 9)
            return $"« {rawTfm} » nécessite un SDK plus récent que .NET 8";
        if (Regex.IsMatch(t, @"^net\d+$"))
            return $"« {rawTfm} » est du .NET Framework (Windows), non pris en charge sur Linux";
        return $"« {rawTfm} » n'est pas reconnu ou pris en charge";
    }
}
