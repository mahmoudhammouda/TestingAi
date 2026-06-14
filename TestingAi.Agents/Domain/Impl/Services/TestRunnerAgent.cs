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

            var trxPath = Path.Combine(Path.GetTempPath(), $"testresults_{state.SessionId}_{Guid.NewGuid():N}.trx");

            var (exitCode, output, error) = await _runner.RunAsync(
                state.TestProjectPath,
                "dotnet",
                $"test \"{state.TestProjectPath}\" --logger \"trx;LogFileName={trxPath}\" --no-build 2>&1");

            _logger.LogInformation("[Exécution] dotnet test terminé (code={Code})", exitCode);

            // Marquer tous les tests en Running d'abord
            foreach (var tc in state.TestCases)
                await _db.UpdateTestCaseStatusAsync(tc.Id, TestStatus.Running);

            if (File.Exists(trxPath))
            {
                await ParseTrxResultsAsync(trxPath, state);
                File.Delete(trxPath);
            }
            else
            {
                _logger.LogWarning("[Exécution] Fichier TRX introuvable. Utilisation de la sortie console.");
                ParseConsoleOutput(output + error, state);
            }

            // Sauvegarder les statuts
            foreach (var tc in state.TestCases)
                await _db.UpdateTestCaseStatusAsync(tc.Id, tc.Status, tc.ErrorMessage);

            int green = state.TestCases.Count(t => t.Status == TestStatus.Green);
            int red = state.TestCases.Count(t => t.Status == TestStatus.Red);
            _logger.LogInformation("[Exécution] Résultats : {Green} verts, {Red} rouges.", green, red);
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
            foreach (var tc in state.TestCases)
            {
                if (output.Contains($"passed") && output.Contains(tc.MethodName))
                    tc.Status = TestStatus.Green;
                else
                {
                    tc.Status = TestStatus.Red;
                    var lines = output.Split('\n');
                    var errLine = lines.FirstOrDefault(l => l.Contains(tc.MethodName) && l.Contains("Failed"));
                    tc.ErrorMessage = errLine?.Trim() ?? "Échec lors de l'exécution.";
                }
            }
        }
    }
}
