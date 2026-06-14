using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class AgentOrchestrator : IAgentOrchestrator
    {
        private readonly IDbContext _db;
        private readonly TestDiscoveryAgent _discovery;
        private readonly TestRunnerAgent _runner;
        private readonly TestDecisionAgent _decision;
        private readonly TestFixerAgent _fixer;
        private readonly ILogger<AgentOrchestrator> _logger;

        public AgentOrchestrator(
            IDbContext db,
            TestDiscoveryAgent discovery,
            TestRunnerAgent runner,
            TestDecisionAgent decision,
            TestFixerAgent fixer,
            ILogger<AgentOrchestrator> logger)
        {
            _db = db;
            _discovery = discovery;
            _runner = runner;
            _decision = decision;
            _fixer = fixer;
            _logger = logger;
        }

        public async Task<AgentState> RunPipelineAsync(string testProjectPath, string sourceProjectPath)
        {
            _logger.LogInformation("[Orchestrateur] Démarrage du pipeline pour : {Path}", testProjectPath);

            var settings = await _db.GetSettingsAsync();
            int sessionId = await _db.CreateSessionAsync(testProjectPath, sourceProjectPath);

            var state = new AgentState
            {
                SessionId = sessionId,
                TestProjectPath = testProjectPath,
                SourceProjectPath = sourceProjectPath,
                TargetProjectPath = testProjectPath,
                Settings = settings,
                PipelineStatus = "En_Cours"
            };

            // Étape 1 : Découverte
            await RunAgentAsync(_discovery, state);

            if (state.TestCases.Count == 0)
            {
                _logger.LogWarning("[Orchestrateur] Aucun test découvert.");
                state.PipelineStatus = "Terminé";
                state.IsFinished = true;
                await SaveSessionState(state);
                return state;
            }

            // Étape 2 : Exécution
            await RunAgentAsync(_runner, state);

            int redCount = state.TestCases.Count(t => t.Status == TestStatus.Red);
            if (redCount == 0)
            {
                _logger.LogInformation("[Orchestrateur] Tous les tests sont verts !");
                state.PipelineStatus = "Terminé";
                state.IsFinished = true;
                await SaveSessionState(state);
                return state;
            }

            // Étape 3 : Décision
            await RunAgentAsync(_decision, state);

            // Mode humain : suspendre ici
            if (state.Settings.HumanInterventionEnabled && state.PipelineStatus == "EnAttenteDecision")
            {
                _logger.LogInformation("[Orchestrateur] Pipeline suspendu — en attente des décisions humaines.");
                await SaveSessionState(state, "EnAttenteDecision");
                return state;
            }

            // Mode auto : continuer
            await RunFixAndVerifyLoopAsync(state);
            return state;
        }

        public async Task<AgentState> ResumePipelineAsync(int sessionId)
        {
            _logger.LogInformation("[Orchestrateur] Reprise du pipeline pour la session {Id}", sessionId);

            var session = await _db.GetSessionAsync(sessionId);
            if (session == null) throw new Exception($"Session {sessionId} introuvable.");

            var testCases = (await _db.GetTestCasesAsync(sessionId)).ToList();
            var settings = await _db.GetSettingsAsync();

            var state = new AgentState
            {
                SessionId = sessionId,
                TestProjectPath = session.TargetProject,
                SourceProjectPath = session.SourceProject,
                TargetProjectPath = session.TargetProject,
                TestCases = testCases,
                Settings = settings,
                PipelineStatus = "En_Cours"
            };

            var pending = state.TestCases.Where(t => t.Status == TestStatus.EnAttenteDecision).ToList();
            if (pending.Count > 0)
                throw new Exception($"{pending.Count} test(s) sont encore en attente de décision humaine.");

            await RunFixAndVerifyLoopAsync(state);
            return state;
        }

        private async Task RunFixAndVerifyLoopAsync(AgentState state)
        {
            int maxRetries = state.Settings.MaxRetries;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var toFix = state.TestCases.Where(t =>
                    (t.Action == TestAction.FixTest || t.Action == TestAction.FixCode) &&
                    t.Status == TestStatus.Red).ToList();

                if (toFix.Count == 0) break;

                _logger.LogInformation("[Orchestrateur] Tentative {N}/{Max}", attempt + 1, maxRetries);

                await RunAgentAsync(_fixer, state);
                await RunAgentAsync(_runner, state);

                int stillRed = state.TestCases.Count(t =>
                    t.Status == TestStatus.Red && t.Action != TestAction.Ignore);
                if (stillRed == 0) break;
            }

            bool allDone = state.TestCases.All(t =>
                t.Status == TestStatus.Green ||
                t.Status == TestStatus.Ignored ||
                t.Action == TestAction.Ignore);

            state.IsFinished = true;
            state.PipelineStatus = allDone ? "Terminé" : "EchecPartiel";

            await SaveSessionState(state, state.PipelineStatus);
            _logger.LogInformation("[Orchestrateur] Pipeline terminé : {Status}", state.PipelineStatus);
        }

        private async Task RunAgentAsync(IAgent agent, AgentState state)
        {
            _logger.LogInformation("[Orchestrateur] → {Name}", agent.Name);
            await _db.LogCommunicationAsync(state.SessionId, agent.Name, "Début");
            try
            {
                await agent.ExecuteAsync(state);
                await _db.LogCommunicationAsync(state.SessionId, agent.Name, "Succès");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrateur] Erreur dans {Name}", agent.Name);
                await _db.LogCommunicationAsync(state.SessionId, agent.Name, $"Erreur : {ex.Message}");
                throw;
            }
        }

        private async Task SaveSessionState(AgentState state, string? status = null)
        {
            var json = JsonConvert.SerializeObject(new
            {
                Total = state.TestCases.Count,
                Green = state.TestCases.Count(t => t.Status == TestStatus.Green),
                Red = state.TestCases.Count(t => t.Status == TestStatus.Red),
                Ignored = state.TestCases.Count(t => t.Status == TestStatus.Ignored),
                AwaitingDecision = state.TestCases.Count(t => t.Status == TestStatus.EnAttenteDecision)
            });
            await _db.UpdateSessionStateAsync(state.SessionId, json, status ?? state.PipelineStatus);
        }
    }
}
