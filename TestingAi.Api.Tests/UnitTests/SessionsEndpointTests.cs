using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Xunit;

namespace TestingAi.Api.Tests.UnitTests;

/// <summary>
/// Tests des endpoints /api/sessions via WebApplicationFactory.
/// Toutes les dépendances (IDbContext, IAgentOrchestrator) sont mockées.
/// </summary>
public class SessionsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SessionsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientWithMocks(
        Mock<IDbContext>? dbMock = null,
        Mock<IAgentOrchestrator>? orchMock = null)
    {
        dbMock ??= BuildDefaultDbMock();
        orchMock ??= new Mock<IAgentOrchestrator>();

        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_ => dbMock.Object);
                services.AddSingleton(_ => orchMock.Object);
            });
        }).CreateClient();
    }

    private static Mock<IDbContext> BuildDefaultDbMock()
    {
        var mock = new Mock<IDbContext>();
        mock.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);
        mock.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new PipelineSettings());
        mock.Setup(d => d.GetAllSessionsAsync())
            .ReturnsAsync(new List<TestingSession>());
        mock.Setup(d => d.GetSessionAsync(It.IsAny<int>()))
            .ReturnsAsync((TestingSession?)null);
        mock.Setup(d => d.GetTestCasesAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<TestCase>());
        mock.Setup(d => d.GetCommunicationsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<AgentA2ACommunication>());
        mock.Setup(d => d.UpdateTestCaseActionAsync(It.IsAny<int>(), It.IsAny<TestAction>()))
            .Returns(Task.CompletedTask);
        mock.Setup(d => d.UpdateSettingsAsync(It.IsAny<PipelineSettings>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task GetSessions_ReturnsOk_WithEmptyList()
    {
        var client = CreateClientWithMocks();

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<object>>();
        Assert.NotNull(sessions);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task GetSessions_ReturnsSessions_WhenExist()
    {
        var dbMock = BuildDefaultDbMock();
        dbMock.Setup(d => d.GetAllSessionsAsync())
              .ReturnsAsync(new List<TestingSession>
              {
                  new() { Id = 1, TargetProject = "/tmp/proj", Status = "Terminé" },
                  new() { Id = 2, TargetProject = "/tmp/proj2", Status = "En_Cours" }
              });

        var client = CreateClientWithMocks(dbMock);

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":1", body);
        Assert.Contains("\"id\":2", body);
    }

    [Fact]
    public async Task GetSession_UnknownId_Returns404()
    {
        var client = CreateClientWithMocks();

        var response = await client.GetAsync("/api/sessions/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSession_KnownId_Returns200()
    {
        var dbMock = BuildDefaultDbMock();
        dbMock.Setup(d => d.GetSessionAsync(42))
              .ReturnsAsync(new TestingSession { Id = 42, TargetProject = "/tmp/p", Status = "Terminé" });
        dbMock.Setup(d => d.GetTestCasesAsync(42))
              .ReturnsAsync(new List<TestCase>());

        var client = CreateClientWithMocks(dbMock);

        var response = await client.GetAsync("/api/sessions/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("42", body);
    }

    [Fact]
    public async Task GetSessionTests_KnownSession_ReturnsTests()
    {
        var dbMock = BuildDefaultDbMock();
        dbMock.Setup(d => d.GetSessionAsync(7))
              .ReturnsAsync(new TestingSession { Id = 7 });
        dbMock.Setup(d => d.GetTestCasesAsync(7))
              .ReturnsAsync(new List<TestCase>
              {
                  new() { Id = 1, MethodName = "Test_A", Status = TestStatus.Green },
                  new() { Id = 2, MethodName = "Test_B", Status = TestStatus.Red }
              });

        var client = CreateClientWithMocks(dbMock);

        var response = await client.GetAsync("/api/sessions/7/tests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test_A", body);
        Assert.Contains("Test_B", body);
    }

    [Fact]
    public async Task GetSessionLogs_Returns200()
    {
        var dbMock = BuildDefaultDbMock();
        dbMock.Setup(d => d.GetSessionAsync(3))
              .ReturnsAsync(new TestingSession { Id = 3 });
        dbMock.Setup(d => d.GetCommunicationsAsync(3))
              .ReturnsAsync(new List<AgentA2ACommunication>
              {
                  new() { Id = 1, SessionId = 3, StepName = "Discovery", ActionSummary = "3 tests trouvés" }
              });

        var client = CreateClientWithMocks(dbMock);

        var response = await client.GetAsync("/api/sessions/3/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Discovery", body);
    }
}
