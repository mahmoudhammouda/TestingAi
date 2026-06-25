using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
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

        var sessionDir = Path.Combine(Path.GetTempPath(), "testingai-sessions", Guid.NewGuid().ToString("N"));

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

        log.LogInformation("Session from-code : répertoire temporaire {Dir}", sessionDir);
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

    var sourceFiles = new List<object>();
    var testFiles = new List<object>();

    bool NotBuildArtifact(string f) =>
        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);

    if (!string.IsNullOrEmpty(session.SourceProject) && Directory.Exists(session.SourceProject))
        foreach (var path in Directory.GetFiles(session.SourceProject, "*.cs", SearchOption.AllDirectories).Where(NotBuildArtifact).OrderBy(f => f))
            sourceFiles.Add(new { fileName = Path.GetFileName(path), content = await File.ReadAllTextAsync(path) });

    if (!string.IsNullOrEmpty(session.TargetProject) && Directory.Exists(session.TargetProject))
        foreach (var path in Directory.GetFiles(session.TargetProject, "*.cs", SearchOption.AllDirectories).Where(NotBuildArtifact).OrderBy(f => f))
            testFiles.Add(new { fileName = Path.GetFileName(path), content = await File.ReadAllTextAsync(path) });

    return Results.Ok(new { sourceFiles, testFiles });
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
