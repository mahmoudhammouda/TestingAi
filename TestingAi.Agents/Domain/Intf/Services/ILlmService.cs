using System.Threading.Tasks;
using TestingAi.Agents.Domain.Intf.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface ILlmService
    {
        Task<string> AskAsync(int sessionId, string agentName, string prompt, string? systemMessage = null, LlmProviderType? preferredProvider = null);
    }
}
