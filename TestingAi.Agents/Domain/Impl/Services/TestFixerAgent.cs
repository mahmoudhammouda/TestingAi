using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        // Garde pratique : nombre maximum de tests détaillés dans un même prompt de
        // correction groupée, pour éviter un contexte LLM surchargé. Le surplus est
        // repris automatiquement à la tentative suivante de la boucle d'auto-correction.
        private const int MaxTestsPerGroup = 10;

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

            // Avertir pour les tests dont le fichier cible est introuvable (ils restent rouges).
            foreach (var tc in toFix)
            {
                var p = ResolveTargetPath(tc);
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                    _logger.LogWarning("[Correction] Fichier introuvable pour {Test} : {Path}", tc.MethodName, p);
            }

            // Regrouper par (Action, chemin cible normalisé) : plusieurs tests rouges qui
            // pointent vers le MÊME fichier sont corrigés en UN SEUL appel LLM. Auparavant
            // l'agent faisait un appel par test : quand un correctif couvrait déjà plusieurs
            // tests, les appels suivants repartaient quand même et le LLM renvoyait le
            // fichier inchangé (appels « no-op » gaspillés observés dans la communication).
            var groups = toFix
                .Select(tc => new { tc, path = ResolveTargetPath(tc) })
                .Where(x => !string.IsNullOrEmpty(x.path) && File.Exists(x.path))
                .GroupBy(x => (x.tc.Action, FullPath: Path.GetFullPath(x.path)))
                .ToList();

            foreach (var group in groups)
            {
                var action = group.Key.Action;
                var fullPath = group.Key.FullPath;
                var groupTests = group.Select(x => x.tc).ToList();

                // Découpage en lots bornés : un fichier avec beaucoup de tests en échec est
                // corrigé en plusieurs appels LLM successifs (chacun repart du correctif du
                // lot précédent) plutôt qu'en un seul prompt surchargé. Cas courant (≤ seuil) :
                // un seul lot = un seul appel.
                foreach (var chunk in groupTests.Chunk(MaxTestsPerGroup))
                {
                    try
                    {
                        var originalCode = await File.ReadAllTextAsync(fullPath);
                        var fixedCode = await AskForGroupedFixAsync(state.SessionId, action, fullPath, chunk, originalCode);

                        if (!string.IsNullOrEmpty(fixedCode) && fixedCode != originalCode)
                        {
                            await File.WriteAllTextAsync(fullPath, fixedCode);
                            foreach (var tc in chunk)
                            {
                                tc.RetryCount++;
                                tc.FixApplied = $"Correction groupée appliquée (tentative {tc.RetryCount})";
                                await _db.UpdateTestCaseFixAsync(tc.Id, tc.FixApplied, tc.RetryCount);
                            }
                            _logger.LogInformation("[Correction] {File} corrigé pour {Count} test(s) ({Action})",
                                Path.GetFileName(fullPath), chunk.Length, action);
                        }
                        else
                        {
                            _logger.LogWarning("[Correction] Le LLM n'a pas modifié {File} ({Count} test(s) à corriger)",
                                Path.GetFileName(fullPath), chunk.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Correction] Erreur lors de la correction groupée de {File}",
                            Path.GetFileName(fullPath));
                    }
                }
            }

            // Ignorer les tests marqués Ignore
            foreach (var tc in state.TestCases.Where(t => t.Action == TestAction.Ignore && t.Status != TestStatus.Ignored))
            {
                tc.Status = TestStatus.Ignored;
                await _db.UpdateTestCaseStatusAsync(tc.Id, TestStatus.Ignored);
            }
        }

        private static string ResolveTargetPath(TestCase tc) =>
            tc.Action == TestAction.FixTest ? tc.TestFilePath : tc.SourceFilePath;

        private async Task<string> AskForGroupedFixAsync(
            int sessionId, TestAction action, string filePath, IReadOnlyList<TestCase> tests, string originalCode)
        {
            var target = action == TestAction.FixTest ? "test" : "code source";
            var fileName = Path.GetFileName(filePath);

            // Le lot est déjà borné par l'appelant (cf. MaxTestsPerGroup) : on détaille tous
            // les tests du lot.
            var sb = new StringBuilder();
            int i = 1;
            foreach (var tc in tests)
            {
                sb.AppendLine($"{i}. Test : {tc.TestName}");
                sb.AppendLine($"   Erreur : {FirstLines(tc.ErrorMessage, 4)}");
                i++;
            }

            // IMPORTANT : la première ligne « Objectif : … » est un en-tête STABLE.
            // L'interface (dashboard Angular) le lit pour afficher la raison de l'appel LLM.
            var prompt =
$@"Objectif : Corriger le {target} {fileName} pour {tests.Count} test(s) en échec

Tu dois corriger ce fichier C# ({target}) afin de satisfaire TOUS les tests listés ci-dessous,
sans casser les comportements déjà corrects.

Tests en échec ({tests.Count}) :
{sb.ToString().TrimEnd()}

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

        private static string FirstLines(string? s, int n)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Aucun message d'erreur.";
            var lines = s.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(n);
            return string.Join("\n   ", lines);
        }
    }
}
