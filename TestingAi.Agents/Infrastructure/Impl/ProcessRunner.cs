using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Infrastructure.Impl
{
    public class ProcessRunner : IProcessRunner
    {
        public async Task<(int ExitCode, string Output, string Error)> RunAsync(string workingDirectory, string fileName, string arguments, int timeoutSeconds = 0)
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

            // Lecture concurrente des flux pour éviter tout blocage si les buffers se remplissent.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (timeoutSeconds > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Processus bloqué (ex. test en interblocage) : on tue tout l'arbre.
                    try { process.Kill(entireProcessTree: true); } catch { }

                    // Nettoyage BORNÉ : ne jamais laisser le chemin de timeout se re-bloquer
                    // (kill partiel, descendant survivant avec des handles hérités, etc.).
                    await WaitBounded(process, 5);
                    string killedOut = await SafeRead(outputTask, 5);
                    string killedErr = await SafeRead(errorTask, 5);
                    return (-1, killedOut,
                        $"Délai dépassé : le processus a été interrompu après {timeoutSeconds}s " +
                        $"(exécution probablement bloquée ou en interblocage).\n{killedErr}");
                }
            }
            else
            {
                await process.WaitForExitAsync();
            }

            string output = await SafeRead(outputTask);
            string error = await SafeRead(errorTask);
            return (process.ExitCode, output, error);
        }

        // Attend la fin du processus avec une borne stricte ; n'échoue jamais.
        private static async Task WaitBounded(Process process, int timeoutSeconds)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                await process.WaitForExitAsync(cts.Token);
            }
            catch { }
        }

        // Lit un flux ; si timeoutSeconds > 0 et que le drain ne se termine pas, renvoie vide.
        private static async Task<string> SafeRead(Task<string> readTask, int timeoutSeconds = 0)
        {
            try
            {
                if (timeoutSeconds > 0)
                {
                    var done = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
                    if (done != readTask) return string.Empty;
                }
                return await readTask;
            }
            catch { return string.Empty; }
        }
    }
}
