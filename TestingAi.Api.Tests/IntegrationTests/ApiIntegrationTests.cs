using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Xunit;

namespace TestingAi.Api.Tests.IntegrationTests;

/// <summary>
/// Tests d'intégration de l'API REST complète.
/// Valide les contrats HTTP (codes de statut, format JSON, logique métier) via WebApplicationFactory.
/// Les dépendances (IDbContext, IAgentOrchestrator) sont remplacées par des mocks.
/// </summary>
public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient(Mock<IDbContext>? dbMock = null, Mock<IAgentOrchestrator>? orchMock = null)
    {
        dbMock ??= BuildDefaultDbMock();
        orchMock ??= new Mock<IAgentOrchestrator>();

        return _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
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
        mock.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new PipelineSettings
        {
            HumanInterventionEnabled = false,
            MaxRetries = 3,
            PreferredProvider = "OpenAi"
        });
        mock.Setup(d => d.GetAllSessionsAsync()).ReturnsAsync(new List<TestingSession>());
        mock.Setup(d => d.GetSessionAsync(It.IsAny<int>())).ReturnsAsync((TestingSession?)null);
        mock.Setup(d => d.GetTestCasesAsync(It.IsAny<int>())).ReturnsAsync(new List<TestCase>());
        mock.Setup(d => d.GetCommunicationsAsync(It.IsAny<int>())).ReturnsAsync(new List<AgentA2ACommunication>());
        mock.Setup(d => d.UpdateTestCaseActionAsync(It.IsAny<int>(), It.IsAny<TestAction>())).Returns(Task.CompletedTask);
        mock.Setup(d => d.UpdateSettingsAsync(It.IsAny<PipelineSettings>())).Returns(Task.CompletedTask);
        return mock;
    }

    // ── SETTINGS ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_Returns200_WithDefaultValues()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("humanInterventionEnabled", body, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maxRetries", body, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenAi", body);
    }

    [Fact]
    public async Task PutSettings_ValidPayload_Returns200()
    {
        var dbMock = BuildDefaultDbMock();
        PipelineSettings? savedSettings = null;
        dbMock.Setup(d => d.UpdateSettingsAsync(It.IsAny<PipelineSettings>()))
              .Callback<PipelineSettings>(s => savedSettings = s)
              .Returns(Task.CompletedTask);

        var client = CreateClient(dbMock);
        var payload = new
        {
            humanInterventionEnabled = true,
            maxRetries = 5,
            preferredProvider = "OpenAi"
        };

        var response = await client.PutAsJsonAsync("/api/settings", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(savedSettings);
        Assert.True(savedSettings.HumanInterventionEnabled);
        Assert.Equal(5, savedSettings.MaxRetries);
    }

    [Fact]
    public async Task PutSettings_InvalidProvider_Returns400()
    {
        var client = CreateClient();
        var payload = new
        {
            humanInterventionEnabled = false,
            maxRetries = 3,
            preferredProvider = "PROVIDER_INEXISTANT"
        };

        var response = await client.PutAsJsonAsync("/api/settings", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── SESSIONS ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostSession_OrchestratorSucceeds_Returns200()
    {
        var orchMock = new Mock<IAgentOrchestrator>();
        orchMock.Setup(o => o.RunPipelineAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new AgentState
                {
                    SessionId = 1,
                    PipelineStatus = "Terminé",
                    IsFinished = true
                });

        var client = CreateClient(orchMock: orchMock);
        var payload = new { testProjectPath = "/some/path", sourceProjectPath = "" };

        var response = await client.PostAsJsonAsync("/api/sessions", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Terminé", body);
    }

    [Fact]
    public async Task PostSession_OrchestratorThrows_Returns500()
    {
        var orchMock = new Mock<IAgentOrchestrator>();
        orchMock.Setup(o => o.RunPipelineAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new Exception("Répertoire introuvable"));

        var client = CreateClient(orchMock: orchMock);
        var payload = new { testProjectPath = "/chemin/inexistant", sourceProjectPath = "" };

        var response = await client.PostAsJsonAsync("/api/sessions", payload);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PutTestAction_ValidAction_Returns200()
    {
        var dbMock = BuildDefaultDbMock();
        dbMock.Setup(d => d.GetTestCaseAsync(1))
              .ReturnsAsync(new TestCase { Id = 1, MethodName = "SomeTest", Status = TestStatus.Red });

        var client = CreateClient(dbMock);
        var payload = new { action = "FixCode" };

        var response = await client.PutAsJsonAsync("/api/tests/1/action", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        dbMock.Verify(d => d.UpdateTestCaseActionAsync(1, TestAction.FixCode), Times.Once);
    }

    [Fact]
    public async Task PutTestAction_InvalidAction_Returns400()
    {
        var dbMock = BuildDefaultDbMock();
        dbMock.Setup(d => d.GetTestCaseAsync(1))
              .ReturnsAsync(new TestCase { Id = 1 });

        var client = CreateClient(dbMock);
        var payload = new { action = "ActionInvalide" };

        var response = await client.PutAsJsonAsync("/api/tests/1/action", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutBulkAction_MultipleTests_AppliesActionToAll()
    {
        var dbMock = BuildDefaultDbMock();
        var actionsApplied = new List<(int, TestAction)>();

        // Nécessaire : le handler fait GetTestCaseAsync → skip si null
        dbMock.Setup(d => d.GetTestCaseAsync(It.IsAny<int>()))
              .ReturnsAsync(new TestCase { Id = 1, MethodName = "ATest", Status = TestStatus.Red });

        dbMock.Setup(d => d.UpdateTestCaseActionAsync(It.IsAny<int>(), It.IsAny<TestAction>()))
              .Callback<int, TestAction>((id, a) => actionsApplied.Add((id, a)))
              .Returns(Task.CompletedTask);

        var client = CreateClient(dbMock);
        var payload = new { testIds = new[] { 1, 2, 3 }, action = "Ignore" };

        var response = await client.PutAsJsonAsync("/api/tests/bulk-action", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, actionsApplied.Count);
        Assert.All(actionsApplied, a => Assert.Equal(TestAction.Ignore, a.Item2));
    }

    // ── HEALTHCHECK BASIQUE ───────────────────────────────────────────────────

    [Fact]
    public async Task Swagger_IsAvailable_InDevelopment()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.AddSingleton(_ => BuildDefaultDbMock().Object);
                services.AddSingleton(_ => new Mock<IAgentOrchestrator>().Object);
            });
            b.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
        }).CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
