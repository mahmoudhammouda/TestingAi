using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Impl.Services;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Xunit;

namespace TestingAi.Agents.Tests.UnitTests;

/// <summary>
/// Tests unitaires de l'AgentOrchestrator.
/// Les agents concrets sont instanciés avec des dépendances mockées pour
/// contrôler leur comportement sans appels LLM ni dotnet test réels.
/// </summary>
public class AgentOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IDbContext> _dbMock;
    private readonly Mock<ILlmService> _llmMock;
    private readonly Mock<IProcessRunner> _runnerMock;

    public AgentOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _dbMock = new Mock<IDbContext>();
        _dbMock.Setup(d => d.CreateSessionAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(1);
        _dbMock.Setup(d => d.GetSessionAsync(It.IsAny<int>())).ReturnsAsync(new TestingSession { Id = 1 });
        _dbMock.Setup(d => d.UpdateSessionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.UpsertTestCaseAsync(It.IsAny<TestCase>())).ReturnsAsync(1);
        _dbMock.Setup(d => d.UpdateTestCaseStatusAsync(It.IsAny<int>(), It.IsAny<TestStatus>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.UpdateTestCaseActionAsync(It.IsAny<int>(), It.IsAny<TestAction>())).Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.UpdateTestCaseFixAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.LogCommunicationAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.SavePrivateMemoryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new PipelineSettings
        {
            HumanInterventionEnabled = false,
            MaxRetries = 3,
            PreferredProvider = "OpenAi"
        });

        _llmMock = new Mock<ILlmService>();
        _runnerMock = new Mock<IProcessRunner>();
    }

    private AgentOrchestrator BuildOrchestrator()
    {
        var analyzer = new AnalyzerAgent(_dbMock.Object, _llmMock.Object, NullLogger<AnalyzerAgent>.Instance);
        var decider = new DeciderAgent(_dbMock.Object, _llmMock.Object, NullLogger<DeciderAgent>.Instance);
        var creator = new CreatorAgent(_dbMock.Object, _llmMock.Object, NullLogger<CreatorAgent>.Instance);
        var exporter = new ExporterAgent(_dbMock.Object, _llmMock.Object, NullLogger<ExporterAgent>.Instance);
        var verifier = new VerifierAgent(_dbMock.Object, _llmMock.Object, _runnerMock.Object, NullLogger<VerifierAgent>.Instance);
        var reviewer = new ReviewerAgent(_dbMock.Object, _llmMock.Object, NullLogger<ReviewerAgent>.Instance);
        var sourceAdapter = new SourceAdapterAgent(_dbMock.Object, _llmMock.Object, NullLogger<SourceAdapterAgent>.Instance);

        var discovery = new TestDiscoveryAgent(_dbMock.Object, NullLogger<TestDiscoveryAgent>.Instance);
        var runner = new TestRunnerAgent(_dbMock.Object, _runnerMock.Object, NullLogger<TestRunnerAgent>.Instance);
        var decision = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var fixer = new TestFixerAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestFixerAgent>.Instance);

        return new AgentOrchestrator(
            _dbMock.Object,
            analyzer, decider, creator, exporter, verifier, reviewer, sourceAdapter,
            discovery, runner, decision, fixer,
            NullLogger<AgentOrchestrator>.Instance
        );
    }

    [Fact]
    public async Task NoTestsDiscovered_PipelineTerminatesImmediately()
    {
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.RunPipelineAsync(_tempDir, _tempDir);

        Assert.True(result.IsFinished);
        Assert.Equal("Terminé", result.PipelineStatus);
        _runnerMock.Verify(
            r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task AllTestsPass_NoLlmCallsRequired()
    {
        var testFile = Path.Combine(_tempDir, "CalculatorTests.cs");
        await File.WriteAllTextAsync(testFile, @"
using Xunit;
public class CalculatorTests
{
    [Fact]
    public void Add_Returns_CorrectSum() { }
}");

        _runnerMock.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                   .ReturnsAsync((0, "1 passed; 0 failed\nAdd_Returns_CorrectSum", ""));

        var orchestrator = BuildOrchestrator();
        var result = await orchestrator.RunPipelineAsync(_tempDir, _tempDir);

        Assert.True(result.IsFinished);
        Assert.Equal("Terminé", result.PipelineStatus);
        _llmMock.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()), Times.Never);
    }

    [Fact]
    public async Task HumanInterventionEnabled_PipelineSuspendsAtDecision()
    {
        _dbMock.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new PipelineSettings
        {
            HumanInterventionEnabled = true,
            MaxRetries = 3,
            PreferredProvider = "OpenAi"
        });

        var testFile = Path.Combine(_tempDir, "UserServiceTests.cs");
        await File.WriteAllTextAsync(testFile, @"
using Xunit;
public class UserServiceTests
{
    [Fact]
    public void GetUser_Returns_Null_WhenNotFound() { }
}");

        _runnerMock.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                   .ReturnsAsync((1, "1 failed - GetUser_Returns_Null_WhenNotFound", ""));

        var orchestrator = BuildOrchestrator();
        var result = await orchestrator.RunPipelineAsync(_tempDir, _tempDir);

        Assert.Equal("EnAttenteDecision", result.PipelineStatus);
        _llmMock.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()), Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}
