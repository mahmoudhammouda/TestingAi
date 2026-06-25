using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Impl.Services;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;
using TestingAi.Agents.Infrastructure.Impl;
using TestingAi.Agents.Tests.E2eTests.Fixtures;
using Xunit;

namespace TestingAi.Agents.Tests.E2eTests;

/// <summary>
/// Tests E2e du pipeline agentic.
/// Ces tests utilisent un vrai projet .NET temporaire, une vraie base SQLite
/// et un IProcessRunner réel. Seul le LLM est stubbé pour le déterminisme.
/// Marqués [Trait("Category","E2e")] — lancer avec : dotnet test --filter "Category=E2e"
/// </summary>
public class PipelineE2eTests : IDisposable
{
    private readonly TempProjectFixture _project;
    private readonly Mock<ILlmService> _llmStub;
    private readonly string _dbPath;

    public PipelineE2eTests()
    {
        _project = new TempProjectFixture();
        _llmStub = new Mock<ILlmService>();
        _dbPath = Path.Combine(Path.GetTempPath(), $"e2e_test_{Guid.NewGuid():N}.db");
    }

    /// <summary>
    /// Crée la DB, l'initialise, et retourne le DbContext + l'orchestrateur.
    /// Le DbContext est retourné pour permettre aux tests de modifier les settings
    /// avant de lancer le pipeline.
    /// </summary>
    private async Task<(AgentOrchestrator orchestrator, DbContext db)> BuildAsync()
    {
        var db = new DbContext(_dbPath);
        await db.InitializeAsync();

        var runner = new ProcessRunner();
        var analyzer = new AnalyzerAgent(db, _llmStub.Object, NullLogger<AnalyzerAgent>.Instance);
        var decider = new DeciderAgent(db, _llmStub.Object, NullLogger<DeciderAgent>.Instance);
        var creator = new CreatorAgent(db, _llmStub.Object, NullLogger<CreatorAgent>.Instance);
        var exporter = new ExporterAgent(db, _llmStub.Object, NullLogger<ExporterAgent>.Instance);
        var verifier = new VerifierAgent(db, _llmStub.Object, runner, NullLogger<VerifierAgent>.Instance);
        var reviewer = new ReviewerAgent(db, _llmStub.Object, NullLogger<ReviewerAgent>.Instance);
        var sourceAdapter = new SourceAdapterAgent(db, _llmStub.Object, NullLogger<SourceAdapterAgent>.Instance);
        var discovery = new TestDiscoveryAgent(db, NullLogger<TestDiscoveryAgent>.Instance);
        var testRunner = new TestRunnerAgent(db, runner, NullLogger<TestRunnerAgent>.Instance);
        var decision = new TestDecisionAgent(db, _llmStub.Object, NullLogger<TestDecisionAgent>.Instance);
        var fixer = new TestFixerAgent(db, _llmStub.Object, NullLogger<TestFixerAgent>.Instance);
        var orchestrator = new AgentOrchestrator(db, analyzer, decider, creator, exporter, verifier, reviewer, sourceAdapter, discovery, testRunner, decision, fixer, NullLogger<AgentOrchestrator>.Instance);

        return (orchestrator, db);
    }

    // ── Scénario 1 ─────────────────────────────────────────────────────────────

    [Fact(Timeout = 120_000)]
    [Trait("Category", "E2e")]
    public async Task AllTestsPassFromStart_PipelineTerminesWithoutLlm()
    {
        await _project.CreatePassingProjectAsync();
        var (orchestrator, _) = await BuildAsync();

        var state = await orchestrator.RunPipelineAsync(_project.ProjectPath, _project.ProjectPath);

        Assert.True(state.IsFinished);
        Assert.Equal("Terminé", state.PipelineStatus);
        Assert.All(state.TestCases, tc => Assert.Equal(TestStatus.Green, tc.Status));

        _llmStub.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()),
            Times.Never,
            "Le LLM ne doit pas être appelé si tous les tests passent.");
    }

    // ── Scénario 2 ─────────────────────────────────────────────────────────────

    [Fact(Timeout = 120_000)]
    [Trait("Category", "E2e")]
    public async Task BuggyProject_HumanMode_PipelineSuspendsAtDecision()
    {
        await _project.CreateBuggyProjectAsync();
        var (orchestrator, db) = await BuildAsync();

        // Configurer explicitement HumanInterventionEnabled = true dans la vraie DB
        // (la DB s'initialise avec false par défaut)
        await db.UpdateSettingsAsync(new PipelineSettings
        {
            HumanInterventionEnabled = true,
            MaxRetries = 3,
            PreferredProvider = "OpenAi"
        });

        var state = await orchestrator.RunPipelineAsync(_project.ProjectPath, _project.ProjectPath);

        Assert.Equal("EnAttenteDecision", state.PipelineStatus);
        Assert.Contains(state.TestCases, tc => tc.Status == TestStatus.EnAttenteDecision);

        _llmStub.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()),
            Times.Never,
            "En mode humain, le LLM ne doit pas être consulté.");
    }

    // ── Scénario 3 ─────────────────────────────────────────────────────────────

    [Fact(Timeout = 180_000)]
    [Trait("Category", "E2e")]
    public async Task BuggyProject_AutoMode_LlmFixesCode_PipelineTermines()
    {
        await _project.CreateBuggyProjectAsync();
        var originalSource = await File.ReadAllTextAsync(_project.BuggySourceFile);
        var (orchestrator, _) = await BuildAsync();

        // Le LLM décide : c'est un bug dans le code source (FixCode)
        _llmStub.Setup(l => l.AskAsync(
                It.IsAny<int>(),
                It.Is<string>(n => n.Contains("Decision")),
                It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
            .ReturnsAsync(@"{""action"": ""FixCode""}");

        // Le LLM corrige : remplace a - b par a + b
        _llmStub.Setup(l => l.AskAsync(
                It.IsAny<int>(),
                It.Is<string>(n => n.Contains("Fixer")),
                It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
            .ReturnsAsync(originalSource.Replace("a - b", "a + b"));

        var state = await orchestrator.RunPipelineAsync(_project.ProjectPath, _project.ProjectPath);

        Assert.True(state.IsFinished);
        Assert.Equal("Terminé", state.PipelineStatus);

        var correctedCode = await File.ReadAllTextAsync(_project.BuggySourceFile);
        Assert.Contains("a + b", correctedCode);
        Assert.DoesNotContain("a - b", correctedCode);
    }

    public void Dispose()
    {
        _project.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
