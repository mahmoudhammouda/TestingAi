using System.Collections.Generic;
using System.Threading.Tasks;
using TestingAi.Agents.Domain.Impl.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IDbContext
    {
        Task InitializeAsync();

        // Sessions
        Task<int> CreateSessionAsync(string targetProject, string sourceProject);
        Task<TestingSession?> GetSessionAsync(int sessionId);
        Task<IEnumerable<TestingSession>> GetAllSessionsAsync();
        Task UpdateSessionStateAsync(int sessionId, string globalState, string status);
        Task UpdateSessionGenerationAsync(int sessionId, string metadata, string testStrategy);
        Task UpdateSessionCodeAsync(int sessionId, string codeSnapshot);
        Task ResetSessionForRerunAsync(int sessionId);

        // TestCases
        Task<int> UpsertTestCaseAsync(TestCase testCase);
        Task<TestCase?> GetTestCaseAsync(int id);
        Task<IEnumerable<TestCase>> GetTestCasesAsync(int sessionId);
        Task UpdateTestCaseStatusAsync(int id, TestStatus status, string? errorMessage = null);
        Task UpdateTestCaseActionAsync(int id, TestAction action);
        Task UpdateTestCaseFixAsync(int id, string fixApplied, int retryCount);

        // Settings
        Task<PipelineSettings> GetSettingsAsync();
        Task UpdateSettingsAsync(PipelineSettings settings);

        // Private memory
        Task SavePrivateMemoryAsync(int sessionId, string agentName, string role, string content);
        Task<IEnumerable<AgentPrivateMemory>> GetPrivateMemoryAsync(int sessionId);

        // Logs
        Task LogCommunicationAsync(int sessionId, string stepName, string actionSummary);
        Task LogCommunicationAsync(int sessionId, string stepName, string actionSummary, string? commandText, string? commandOutput);
        Task<IEnumerable<AgentA2ACommunication>> GetCommunicationsAsync(int sessionId);
    }
}
