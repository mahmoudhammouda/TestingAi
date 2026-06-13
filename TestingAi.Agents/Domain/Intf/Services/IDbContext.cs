using System.Collections.Generic;
using System.Threading.Tasks;
using TestingAi.Agents.Domain.Impl.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IDbContext
    {
        Task<int> CreateSessionAsync(string targetProject);
        Task UpdateSessionStateAsync(int sessionId, string state, string status);
        Task<TestingSession> GetSessionAsync(int sessionId);
        Task LogCommunicationAsync(int sessionId, string stepName, string actionSummary);
        Task<IEnumerable<AgentA2ACommunication>> GetCommunicationsAsync(int sessionId);
        Task SavePrivateMemoryAsync(int sessionId, string agentName, string role, string content);
        Task<IEnumerable<AgentPrivateMemory>> GetPrivateMemoryAsync(int sessionId, string agentName);
    }
}
