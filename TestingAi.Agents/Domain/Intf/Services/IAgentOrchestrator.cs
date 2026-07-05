using System.Collections.Generic;
using System.Threading.Tasks;
using TestingAi.Agents.Domain.Impl.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IAgentOrchestrator
    {
        Task<AgentState> RunPipelineAsync(string testProjectPath, string sourceProjectPath, string? generateFromSourceFile = null);

        // Exécute le pipeline pour une session DÉJÀ créée, en éventail sur plusieurs fichiers
        // source (import de dossier). Utilisé par l'exécution en arrière-plan.
        Task<AgentState> RunPipelineForSessionAsync(int sessionId, string testProjectPath, string sourceProjectPath, IReadOnlyList<string> sourceFiles);

        Task<AgentState> ResumePipelineAsync(int sessionId);
        Task<AgentState> RerunPipelineAsync(int sessionId);
    }
}
