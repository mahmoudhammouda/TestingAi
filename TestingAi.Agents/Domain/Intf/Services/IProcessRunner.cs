using System.Diagnostics;
using System.Threading.Tasks;

namespace TestingAi.Agents.Domain.Intf.Services
{
    public interface IProcessRunner
    {
        // timeoutSeconds <= 0 : pas de limite. Sinon le processus (et son arbre) est tué au-delà du délai.
        Task<(int ExitCode, string Output, string Error)> RunAsync(string workingDirectory, string fileName, string arguments, int timeoutSeconds = 0);
    }
}
