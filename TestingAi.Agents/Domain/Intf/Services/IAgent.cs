using System.Threading.Tasks;
using TestingAi.Agents.Domain.Impl.Models;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IAgent
    {
        string Name { get; }
        Task ExecuteAsync(AgentState state);
    }
}
