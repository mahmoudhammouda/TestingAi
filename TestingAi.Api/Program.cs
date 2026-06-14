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
using TestingAi.Agents.Domain.Intf.Services;
using TestingAi.Agents.Infrastructure.Impl;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var config = builder.Configuration;
var dbPath = config["DbPath"] ?? "Data/TestingAi.Agents.db";

builder.Services.AddSingleton<IDbContext>(sp => new DbContext(dbPath));
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<ILlmProvider>(sp =>
    new GeminiLlmProvider(config["GeminiApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")));
builder.Services.AddSingleton<ILlmService, LlmService>();
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
    if (action != TestAction.Ignore)
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
        if (action != TestAction.Ignore) await dbCtx.UpdateTestCaseStatusAsync(id, TestStatus.Red);
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

app.MapPut("/api/settings", async (UpdateSettingsRequest req, IDbContext dbCtx) =>
{
    var s = new PipelineSettings
    {
        HumanInterventionEnabled = req.HumanInterventionEnabled,
        MaxRetries = req.MaxRetries,
        PreferredProvider = req.PreferredProvider ?? "Gemini"
    };
    await dbCtx.UpdateSettingsAsync(s);
    return Results.Ok(new { message = "Paramètres mis à jour.", s.HumanInterventionEnabled, s.MaxRetries, s.PreferredProvider });
}).WithName("UpdateSettings").WithOpenApi();

// ── LOGS ──────────────────────────────────────────────────────────────────────

app.MapGet("/api/sessions/{id:int}/logs", async (int id, IDbContext dbCtx) =>
    Results.Ok(await dbCtx.GetCommunicationsAsync(id)))
    .WithName("GetSessionLogs").WithOpenApi();

app.Run();

record StartSessionRequest(string TestProjectPath, string? SourceProjectPath);
record SetActionRequest(string Action);
record BulkActionRequest(int[] TestIds, string Action);
record UpdateSettingsRequest(bool HumanInterventionEnabled, int MaxRetries, string? PreferredProvider);
