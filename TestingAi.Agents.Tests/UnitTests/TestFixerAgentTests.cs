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

public class TestFixerAgentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IDbContext> _dbMock;
    private readonly Mock<ILlmService> _llmMock;
    private readonly TestFixerAgent _agent;

    public TestFixerAgentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fixer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _dbMock = new Mock<IDbContext>();
        _dbMock.Setup(d => d.UpdateTestCaseFixAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
               .Returns(Task.CompletedTask);
        _dbMock.Setup(d => d.LogCommunicationAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
               .Returns(Task.CompletedTask);

        _llmMock = new Mock<ILlmService>();
        _agent = new TestFixerAgent(_dbMock.Object, _llmMock.Object, NullLogger<TestFixerAgent>.Instance);
    }

    private async Task<(TestCase testCase, string filePath)> CreateTestCaseWithFile(
        string originalCode,
        TestAction action = TestAction.FixTest)
    {
        var filePath = Path.Combine(_tempDir, $"SomeTest_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, originalCode);

        var tc = new TestCase
        {
            Id = 10,
            MethodName = "SomeTest",
            Status = TestStatus.Red,
            Action = action,
            TestFilePath = action == TestAction.FixTest ? filePath : "",
            SourceFilePath = action == TestAction.FixCode ? filePath : ""
        };

        return (tc, filePath);
    }

    [Fact]
    public async Task LlmReturnsDifferentCode_WritesFile_IncrementsRetryCount()
    {
        const string original = "public void OldTest() { Assert.Equal(2, 1 + 1); }";
        const string corrected = "public void OldTest() { Assert.Equal(2, 1 + 1); /* fixed */ }";

        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync(corrected);

        var (tc, filePath) = await CreateTestCaseWithFile(original, TestAction.FixTest);
        var state = new AgentState { SessionId = 1 };
        state.TestCases.Add(tc);

        await _agent.ExecuteAsync(state);

        var writtenContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal(corrected, writtenContent);
        Assert.Equal(1, tc.RetryCount);
        _dbMock.Verify(d => d.UpdateTestCaseFixAsync(10, It.IsAny<string>(), 1), Times.Once);
    }

    [Fact]
    public async Task LlmReturnsSameCode_DoesNotWriteFile()
    {
        const string original = "public void SameTest() { Assert.Equal(3, 1 + 2); }";

        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync(original);

        var (tc, filePath) = await CreateTestCaseWithFile(original, TestAction.FixTest);

        var state = new AgentState { SessionId = 1 };
        state.TestCases.Add(tc);

        await _agent.ExecuteAsync(state);

        Assert.Equal(0, tc.RetryCount);
        _dbMock.Verify(d => d.UpdateTestCaseFixAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task FileNotFound_SkipsWithoutCrashing()
    {
        var tc = new TestCase
        {
            Id = 99,
            MethodName = "Ghost",
            Status = TestStatus.Red,
            Action = TestAction.FixTest,
            TestFilePath = "/nonexistent/path/GhostTests.cs"
        };

        var state = new AgentState { SessionId = 1 };
        state.TestCases.Add(tc);

        var exception = await Record.ExceptionAsync(() => _agent.ExecuteAsync(state));

        Assert.Null(exception);
        _dbMock.Verify(d => d.UpdateTestCaseFixAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task FixCode_Action_ReadsSourceFile_NotTestFile()
    {
        const string sourceCode = "public class Calculator { public int Add(int a, int b) => a - b; }";
        const string fixedCode = "public class Calculator { public int Add(int a, int b) => a + b; }";

        _llmMock.Setup(l => l.AskAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LlmProviderType?>()))
                .ReturnsAsync(fixedCode);

        var (tc, sourceFilePath) = await CreateTestCaseWithFile(sourceCode, TestAction.FixCode);
        var state = new AgentState { SessionId = 1 };
        state.TestCases.Add(tc);

        await _agent.ExecuteAsync(state);

        var writtenContent = await File.ReadAllTextAsync(sourceFilePath);
        Assert.Equal(fixedCode, writtenContent);
    }

    [Fact]
    public async Task GreenTests_AreNotFixed()
    {
        var tc = new TestCase
        {
            Id = 5,
            MethodName = "PassingTest",
            Status = TestStatus.Green,
            Action = TestAction.FixTest,
            TestFilePath = Path.Combine(_tempDir, "passing.cs")
        };
        await File.WriteAllTextAsync(tc.TestFilePath, "some code");

        var state = new AgentState { SessionId = 1 };
        state.TestCases.Add(tc);

        await _agent.ExecuteAsync(state);

        _llmMock.Verify(l => l.AskAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<LlmProviderType?>()), Times.Never);
    }

    [Fact]
    public async Task IgnoredTests_AreNotFixed()
    {
        var tc = new TestCase
        {
            Id = 6,
            MethodName = "IgnoredTest",
            Status = TestStatus.Red,
            Action = TestAction.Ignore,
            TestFilePath = Path.Combine(_tempDir, "ignored.cs")
        };
        await File.WriteAllTextAsync(tc.TestFilePath, "some code");

        var state = new AgentState { SessionId = 1 };
        state.TestCases.Add(tc);

        await _agent.ExecuteAsync(state);

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
