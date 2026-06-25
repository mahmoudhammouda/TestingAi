using System.Threading.Tasks;
using TestingAi.Agents.Domain.Impl.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IAgentOrchestrator
    {
        Task<AgentState> RunPipelineAsync(string testProjectPath, string sourceProjectPath, string? generateFromSourceFile = null);
        Task<AgentState> ResumePipelineAsync(int sessionId);
        Task<AgentState> RerunPipelineAsync(int sessionId);
    }
}
