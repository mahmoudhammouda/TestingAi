using System.Diagnostics;
using System.Threading.Tasks;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Infrastructure.Impl
{
    public class ProcessRunner : IProcessRunner
    {
        public async Task<(int ExitCode, string Output, string Error)> RunAsync(string workingDirectory, string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return (-1, "", "Impossible de démarrer le processus.");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode, output, error);
        }
    }
}
