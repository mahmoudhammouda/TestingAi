using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class TestRunnerAgent : IAgent
    {
        public string Name => "TestRunnerAgent";
        private readonly IDbContext _db;
        private readonly IProcessRunner _runner;
        private readonly ILogger<TestRunnerAgent> _logger;

        public TestRunnerAgent(IDbContext db, IProcessRunner runner, ILogger<TestRunnerAgent> logger)
        {
            _db = db;
            _runner = runner;
            _logger = logger;
        }

        public async Task ExecuteAsync(AgentState state)
        {
            _logger.LogInformation("[Exécution] Lancement des tests dans : {Path}", state.TestProjectPath);

            var resultsDir = Path.Combine(Path.GetTempPath(), $"trx_{state.SessionId}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(resultsDir);
            var trxPath = Path.Combine(resultsDir, "results.trx");

            var (exitCode, output, error) = await _runner.RunAsync(
                state.TestProjectPath,
                "dotnet",
                $"test \"{state.TestProjectPath}\" --logger \"trx;LogFileName=results.trx\" --results-directory \"{resultsDir}\"",
                timeoutSeconds: 120);

            _logger.LogInformation("[Exécution] dotnet test terminé (code={Code})", exitCode);

            // Marquer tous les tests en Running d'abord
            foreach (var tc in state.TestCases)
                await _db.UpdateTestCaseStatusAsync(tc.Id, TestStatus.Running);

            string? foundTrx = File.Exists(trxPath)
                ? trxPath
                : (Directory.Exists(resultsDir) ? Directory.GetFiles(resultsDir, "*.trx").FirstOrDefault() : null);

            if (foundTrx != null)
            {
                await ParseTrxResultsAsync(foundTrx, state);
            }
            else
            {
                _logger.LogWarning("[Exécution] Fichier TRX introuvable. Utilisation de la sortie console.");
                ParseConsoleOutput(output + "\n" + error, state);
            }

            try { if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, true); } catch { }

            // Sauvegarder les statuts
            foreach (var tc in state.TestCases)
                await _db.UpdateTestCaseStatusAsync(tc.Id, tc.Status, tc.ErrorMessage);

            int green = state.TestCases.Count(t => t.Status == TestStatus.Green);
            int red = state.TestCases.Count(t => t.Status == TestStatus.Red);
            _logger.LogInformation("[Exécution] Résultats : {Green} verts, {Red} rouges.", green, red);

            // Trace de la commande lancée et de son résultat pour la « Communication agentique ».
            var cmd = $"dotnet test \"{state.TestProjectPath}\" --logger \"trx;LogFileName=results.trx\" --results-directory \"{resultsDir}\"";
            var perTest = string.Join("\n", state.TestCases.Select(t =>
                $"  {(t.Status == TestStatus.Green ? "✅" : "❌")} {t.MethodName}" +
                (t.Status == TestStatus.Red && !string.IsNullOrWhiteSpace(t.ErrorMessage)
                    ? $" — {FirstLine(t.ErrorMessage)}" : string.Empty)));
            var combined = string.Join("\n",
                new[] { output, error }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var runResult =
                $"Code de sortie : {exitCode}\n" +
                $"Résultat : {green} test(s) ✅ vert(s), {red} test(s) ❌ rouge(s)\n\n" +
                $"{perTest}\n\n— Sortie console —\n{AgentDiagnostics.Truncate(combined)}";
            await _db.LogCommunicationAsync(
                state.SessionId, Name,
                $"dotnet test : {green} vert(s), {red} rouge(s)", cmd, runResult);
        }

        private static string FirstLine(string s)
        {
            var idx = s.IndexOf('\n');
            return (idx >= 0 ? s.Substring(0, idx) : s).Trim();
        }

        private async Task ParseTrxResultsAsync(string trxPath, AgentState state)
        {
            var xml = await File.ReadAllTextAsync(trxPath);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

            var results = doc.Descendants(ns + "UnitTestResult");
            foreach (var r in results)
            {
                var testName = r.Attribute("testName")?.Value ?? "";
                var outcome = r.Attribute("outcome")?.Value ?? "Failed";
                var errorMsg = r.Descendants(ns + "Message").FirstOrDefault()?.Value;
                var stackTrace = r.Descendants(ns + "StackTrace").FirstOrDefault()?.Value;

                var full = string.IsNullOrEmpty(errorMsg) ? null :
                    (string.IsNullOrEmpty(stackTrace) ? errorMsg : $"{errorMsg}\n\n{stackTrace}");

                // Correspondance par nom de méthode
                var tc = state.TestCases.FirstOrDefault(t =>
                    testName.EndsWith(t.MethodName, StringComparison.OrdinalIgnoreCase) ||
                    testName.Contains(t.MethodName, StringComparison.OrdinalIgnoreCase));

                if (tc != null)
                {
                    tc.Status = outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                        ? TestStatus.Green : TestStatus.Red;
                    tc.ErrorMessage = full;
                }
            }

            // Les tests non trouvés dans TRX → Red
            foreach (var tc in state.TestCases.Where(t => t.Status == TestStatus.Running))
            {
                tc.Status = TestStatus.Red;
                tc.ErrorMessage = "Test non trouvé dans les résultats TRX.";
            }
        }

                private void ParseConsoleOutput(string output, AgentState state)
        {
            var buildErrors = ExtractBuildErrors(output);
            var timeoutLine = output.Split('\n')
                .FirstOrDefault(l => l.Contains("Délai dépassé"))?.Trim();

            foreach (var tc in state.TestCases)
            {
                if (output.Contains($"passed") && output.Contains(tc.MethodName))
                    tc.Status = TestStatus.Green;
                else
                {
                    tc.Status = TestStatus.Red;
                    var lines = output.Split('\n');
                    var errLine = lines.FirstOrDefault(l => l.Contains(tc.MethodName) && l.Contains("Failed"));
                    tc.ErrorMessage = errLine?.Trim()
                        ?? (buildErrors.Length > 0
                            ? $"Erreur de compilation :\n{buildErrors}"
                            : (!string.IsNullOrEmpty(timeoutLine)
                                ? timeoutLine
                                : "Échec lors de l'exécution."));
                }
            }
        }

        private static string ExtractBuildErrors(string output)
        {
            var errorLines = output.Split('\n')
                .Where(l => (l.Contains(": error ") || l.Contains(": Error ")) && l.Trim().Length > 0)
                .Take(8)
                .Select(l => l.Trim());
            return string.Join("\n", errorLines);
        }
    }
}
