using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class TestDecisionAgent : IAgent
    {
        public string Name => "TestDecisionAgent";
        private readonly IDbContext _db;
        private readonly ILlmService _llm;
        private readonly ILogger<TestDecisionAgent> _logger;

        public TestDecisionAgent(IDbContext db, ILlmService llm, ILogger<TestDecisionAgent> logger)
        {
            _db = db;
            _llm = llm;
            _logger = logger;
        }

        public async Task ExecuteAsync(AgentState state)
        {
            var redTests = state.TestCases.Where(t => t.Status == TestStatus.Red && t.Action == TestAction.None).ToList();
            _logger.LogInformation("[Décision] {Count} test(s) rouge(s) à analyser.", redTests.Count);

            if (state.Settings.HumanInterventionEnabled)
            {
                // Mode humain : passer en EnAttenteDecision
                foreach (var tc in redTests)
                {
                    tc.Status = TestStatus.EnAttenteDecision;
                    await _db.UpdateTestCaseStatusAsync(tc.Id, TestStatus.EnAttenteDecision, tc.ErrorMessage);
                }
                state.PipelineStatus = "EnAttenteDecision";
                _logger.LogInformation("[Décision] Pipeline suspendu — en attente des décisions humaines.");
                return;
            }

            // Mode automatique : demander à l'IA pour chaque test
            foreach (var tc in redTests)
            {
                try
                {
                    var action = await DecideWithAiAsync(state.SessionId, tc);
                    tc.Action = action;
                    await _db.UpdateTestCaseActionAsync(tc.Id, action);
                    _logger.LogInformation("[Décision] {Test} → {Action}", tc.MethodName, action);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Décision] Erreur IA pour {Test}, défaut : FixTest", tc.MethodName);
                    tc.Action = TestAction.FixTest;
                    await _db.UpdateTestCaseActionAsync(tc.Id, TestAction.FixTest);
                }
            }
        }

        private async Task<TestAction> DecideWithAiAsync(int sessionId, TestCase tc)
        {
            var prompt = $@"Analyse ce test unitaire C# qui a échoué et décide de l'action à prendre.

Nom du test : {tc.TestName}
Fichier     : {tc.TestFilePath}
Erreur      :
{tc.ErrorMessage ?? "Aucun message d'erreur disponible."}

Choisis UNE action parmi les trois suivantes :
- FixTest  : L'erreur est dans le test lui-même (assertion incorrecte, setup défaillant, mock mal configuré)
- FixCode  : L'erreur est dans le code source testé (régression, bug)
- Ignore   : Le test est obsolète ou non pertinent

Réponds UNIQUEMENT avec un objet JSON valide, sans texte avant ni après :
{{""action"": ""FixTest""}} ou {{""action"": ""FixCode""}} ou {{""action"": ""Ignore""}}";

            var json = await _llm.AskAsync(sessionId, Name, prompt, "Tu es un expert en tests unitaires C#. Réponds uniquement en JSON.");

            // Extraire le JSON
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
                json = json.Substring(start, end - start + 1);

            var obj = JsonConvert.DeserializeObject<dynamic>(json);
            string actionStr = obj?.action?.ToString() ?? "FixTest";

            return Enum.TryParse<TestAction>(actionStr, true, out var action) ? action : TestAction.FixTest;
        }
    }
}
