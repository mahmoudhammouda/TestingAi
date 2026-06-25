using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Impl.Services;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Xunit;

namespace TestingAi.Agents.Tests.UnitTests;

public class TestDecisionAgentTests
{
    private readonly Mock<IDbContext> _dbMock;
    private readonly Mock<ILlmService> _llmMock;

    public TestDecisionAgentTests()
    {
        _dbMock = new Mock<IDbContext>();
        _dbMock.Setup(d => d.UpdateTestCaseStatusAsync(
                It.IsAny<int>(), It.IsAny<TestStatus>(), It.IsAny<string?>()))
               .Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.UpdateTestCaseActionAsync(It.IsAny<int>(), It.IsAny<TestAction>()))
               .Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.LogCommunicationAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
               .Returns(Task.CompletedTask);

        _llmMock = new Mock<ILlmService>();
    }

    private AgentState BuildState(bool humanIntervention = false, TestAction action = TestAction.None)
    {
        var state = new AgentState
        {
            SessionId = 1,
            Settings = new PipelineSettings { HumanInterventionEnabled = humanIntervention }
        };
        state.TestCases.Add(new TestCase
        {
            Id = 42,
            MethodName = "SomeTest",
            Status = TestStatus.Red,
            Action = action,
            ErrorMessage = "Assert.Equal() failure: expected 5 but was 3"
        });
        return state;
    }

    [Fact]
    public async Task HumanMode_SetsAllRedTestsToEnAttenteDecision()
    {
        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: true);

        await agent.ExecuteAsync(state);

        Assert.Equal(TestStatus.EnAttenteDecision, state.TestCases[0].Status);
        Assert.Equal("EnAttenteDecision", state.PipelineStatus);
        _llmMock.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()), Times.Never);
    }

    [Fact]
    public async Task AutoMode_LlmReturnsFixCode_SetsActionToFixCode()
    {
        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync(@"{""action"": ""FixCode""}");

        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: false);

        await agent.ExecuteAsync(state);

        Assert.Equal(TestAction.FixCode, state.TestCases[0].Action);
        _dbMock.Verify(d => d.UpdateTestCaseActionAsync(42, TestAction.FixCode), Times.Once);
    }

    [Fact]
    public async Task AutoMode_LlmReturnsFixTest_SetsActionToFixTest()
    {
        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync(@"{""action"": ""FixTest""}");

        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: false);

        await agent.ExecuteAsync(state);

        Assert.Equal(TestAction.FixTest, state.TestCases[0].Action);
        _dbMock.Verify(d => d.UpdateTestCaseActionAsync(42, TestAction.FixTest), Times.Once);
    }

    [Fact]
    public async Task AutoMode_LlmReturnsIgnore_SetsActionToIgnore()
    {
        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync(@"{""action"": ""Ignore""}");

        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: false);

        await agent.ExecuteAsync(state);

        Assert.Equal(TestAction.Ignore, state.TestCases[0].Action);
    }

    [Fact]
    public async Task AutoMode_LlmThrows_DefaultsToFixTest()
    {
        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ThrowsAsync(new System.Exception("LLM timeout"));

        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: false);

        await agent.ExecuteAsync(state);

        Assert.Equal(TestAction.FixTest, state.TestCases[0].Action);
    }

    [Fact]
    public async Task AutoMode_LlmReturnsInvalidJson_DefaultsToFixTest()
    {
        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync("Je ne sais pas quoi répondre.");

        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: false);

        await agent.ExecuteAsync(state);

        Assert.Equal(TestAction.FixTest, state.TestCases[0].Action);
    }

    [Fact]
    public async Task AlreadyDecidedTests_AreNotReprocessed()
    {
        var agent = new TestDecisionAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestDecisionAgent>.Instance);
        var state = BuildState(humanIntervention: false, action: TestAction.FixTest);

        await agent.ExecuteAsync(state);

        _llmMock.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()), Times.Never);
    }
}
