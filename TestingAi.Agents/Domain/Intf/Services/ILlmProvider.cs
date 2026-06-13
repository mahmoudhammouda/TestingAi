using System.Threading.Tasks;
using TestingAi.Agents.Domain.Intf.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface ILlmProvider
    {
        LlmProviderType ProviderType { get; }
        Task<LlmResponse> AskAsync(string prompt, string? systemMessage = null);
    }
}
