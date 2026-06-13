using System.Diagnostics;
using System.Threading.Tasks;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IProcessRunner
    {
        Task<(int ExitCode, string Output, string Error)> RunAsync(string workingDirectory, string fileName, string arguments);
    }
}
