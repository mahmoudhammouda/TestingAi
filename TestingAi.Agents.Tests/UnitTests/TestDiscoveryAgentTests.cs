using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Impl.Services;
using TestingAi.Agents.Domain.Intf.Services;
using Xunit;

namespace TestingAi.Agents.Tests.UnitTests;

public class TestDiscoveryAgentTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IDbContext> _dbMock;
    private readonly TestDiscoveryAgent _agent;

    public TestDiscoveryAgentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"discovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _dbMock = new Mock<IDbContext>();
        _dbMock.Setup(d => d.UpsertTestCaseAsync(It.IsAny<TestCase>()))
               .ReturnsAsync(1);
        _dbMock.Setup(d => d.LogCommunicationAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
               .Returns(Task.CompletedTask);

        _agent = new TestDiscoveryAgent(_dbMock.Object, NullLogger<TestDiscoveryAgent>.Instance);
    }

    [Fact]
    public async Task Discovers_Fact_Methods_In_CSharp_File()
    {
        var testFile = Path.Combine(_tempDir, "CalculatorTests.cs");
        await File.WriteAllTextAsync(testFile, @"
using Xunit;
public class CalculatorTests
{
    [Fact]
    public void Add_TwoNumbers_ReturnsSum() { }

    [Fact]
    public void Subtract_TwoNumbers_ReturnsDifference() { }

    public void NotATest_ShouldBeIgnored() { }
}");

        var state = new AgentState
        {
            SessionId = 1,
            TestProjectPath = _tempDir,
            SourceProjectPath = _tempDir
        };

        await _agent.ExecuteAsync(state);

        Assert.Equal(2, state.TestCases.Count);
        Assert.Contains(state.TestCases, t => t.MethodName == "Add_TwoNumbers_ReturnsSum");
        Assert.Contains(state.TestCases, t => t.MethodName == "Subtract_TwoNumbers_ReturnsDifference");
    }

    [Fact]
    public async Task Skips_Files_In_Obj_And_Bin_Directories()
    {
        var objDir = Path.Combine(_tempDir, "obj");
        Directory.CreateDirectory(objDir);
        await File.WriteAllTextAsync(Path.Combine(objDir, "ShouldBeIgnoredTests.cs"), @"
using Xunit;
public class ShouldBeIgnoredTests
{
    [Fact]
    public void InObjDir_ShouldNotBeDiscovered() { }
}");

        var realFile = Path.Combine(_tempDir, "RealTests.cs");
        await File.WriteAllTextAsync(realFile, @"
using Xunit;
public class RealTests
{
    [Fact]
    public void ValidTest() { }
}");

        var state = new AgentState
        {
            SessionId = 1,
            TestProjectPath = _tempDir,
            SourceProjectPath = _tempDir
        };

        await _agent.ExecuteAsync(state);

        Assert.Single(state.TestCases);
        Assert.Equal("ValidTest", state.TestCases[0].MethodName);
    }

    [Fact]
    public async Task Discovers_Theory_Methods()
    {
        var testFile = Path.Combine(_tempDir, "MathTests.cs");
        await File.WriteAllTextAsync(testFile, @"
using Xunit;
public class MathTests
{
    [Theory]
    [InlineData(1)]
    public void Square_PositiveNumber_ReturnsPositive(int n) { }
}");

        var state = new AgentState
        {
            SessionId = 1,
            TestProjectPath = _tempDir,
            SourceProjectPath = _tempDir
        };

        await _agent.ExecuteAsync(state);

        Assert.Single(state.TestCases);
        Assert.Equal("Square_PositiveNumber_ReturnsPositive", state.TestCases[0].MethodName);
    }

    [Fact]
    public async Task EmptyDirectory_ProducesNoTestCases()
    {
        var state = new AgentState
        {
            SessionId = 1,
            TestProjectPath = _tempDir,
            SourceProjectPath = _tempDir
        };

        await _agent.ExecuteAsync(state);

        Assert.Empty(state.TestCases);
    }

    [Fact]
    public async Task Associates_ClassName_And_TestFile_With_Each_TestCase()
    {
        var testFile = Path.Combine(_tempDir, "OrderServiceTests.cs");
        await File.WriteAllTextAsync(testFile, @"
using Xunit;
public class OrderServiceTests
{
    [Fact]
    public void PlaceOrder_ValidOrder_Succeeds() { }
}");

        var state = new AgentState
        {
            SessionId = 1,
            TestProjectPath = _tempDir,
            SourceProjectPath = _tempDir
        };

        await _agent.ExecuteAsync(state);

        var tc = Assert.Single(state.TestCases);
        Assert.Equal("OrderServiceTests", tc.ClassName);
        Assert.Equal("PlaceOrder_ValidOrder_Succeeds", tc.MethodName);
        Assert.Equal(testFile, tc.TestFilePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}
