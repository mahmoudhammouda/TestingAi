using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class TestFixerAgent : IAgent
    {
        public string Name => "TestFixerAgent";
        private readonly IDbContext _db;
        private readonly ILlmService _llm;
        private readonly ILogger<TestFixerAgent> _logger;

        public TestFixerAgent(IDbContext db, ILlmService llm, ILogger<TestFixerAgent> logger)
        {
            _db = db;
            _llm = llm;
            _logger = logger;
        }

        public async Task ExecuteAsync(AgentState state)
        {
            var toFix = state.TestCases.Where(t =>
                t.Status == TestStatus.Red &&
                (t.Action == TestAction.FixTest || t.Action == TestAction.FixCode)).ToList();

            _logger.LogInformation("[Correction] {Count} test(s) à corriger.", toFix.Count);

            foreach (var tc in toFix)
            {
                try
                {
                    string filePath = tc.Action == TestAction.FixTest ? tc.TestFilePath : tc.SourceFilePath;

                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    {
                        _logger.LogWarning("[Correction] Fichier introuvable pour {Test} : {Path}", tc.MethodName, filePath);
                        continue;
                    }

                    var originalCode = await File.ReadAllTextAsync(filePath);
                    var fixedCode = await AskForFixAsync(state.SessionId, tc, originalCode);

                    if (!string.IsNullOrEmpty(fixedCode) && fixedCode != originalCode)
                    {
                        await File.WriteAllTextAsync(filePath, fixedCode);
                        tc.RetryCount++;
                        tc.FixApplied = $"Correction appliquée (tentative {tc.RetryCount})";
                        await _db.UpdateTestCaseFixAsync(tc.Id, tc.FixApplied, tc.RetryCount);
                        _logger.LogInformation("[Correction] {Test} corrigé ({Action})", tc.MethodName, tc.Action);
                    }
                    else
                    {
                        _logger.LogWarning("[Correction] LLM n'a pas fourni de correction pour {Test}", tc.MethodName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Correction] Erreur lors de la correction de {Test}", tc.MethodName);
                }
            }

            // Ignorer les tests marqués Ignore
            foreach (var tc in state.TestCases.Where(t => t.Action == TestAction.Ignore && t.Status != TestStatus.Ignored))
            {
                tc.Status = TestStatus.Ignored;
                await _db.UpdateTestCaseStatusAsync(tc.Id, TestStatus.Ignored);
            }
        }

        private async Task<string> AskForFixAsync(int sessionId, TestCase tc, string originalCode)
        {
            var target = tc.Action == TestAction.FixTest ? "test" : "code source";
            var prompt = $@"Tu dois corriger ce fichier C# ({target}).

Test concerné   : {tc.TestName}
Action demandée : {tc.Action}
Erreur          :
{tc.ErrorMessage ?? "Aucun message d'erreur."}

Voici le fichier à corriger :
```csharp
{originalCode}
```

Retourne UNIQUEMENT le fichier C# corrigé complet, sans explication, sans balises markdown, sans texte avant ou après.
Si tu ne peux pas corriger, retourne exactement le code original.";

            var response = await _llm.AskAsync(sessionId, Name, prompt,
                "Tu es un expert en C# et tests unitaires. Tu retournes uniquement du code C# valide.");

            // Nettoyer les éventuelles balises markdown
            var cleaned = Regex.Replace(response, @"^```(?:csharp)?\s*\n?", "", RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, @"\n?```\s*$", "", RegexOptions.Multiline);
            return cleaned.Trim();
        }
    }
}
