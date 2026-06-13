using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Newtonsoft.Json;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class AgentOrchestrator : IAgentOrchestrator
    {
        private readonly IDbContext _dbContext;
        private readonly IEnumerable<IAgent> _agents;
        private readonly ILogger<AgentOrchestrator> _logger;

        public AgentOrchestrator(IDbContext dbContext, IEnumerable<IAgent> agents, ILogger<AgentOrchestrator> logger)
        {
            _dbContext = dbContext;
            _agents = agents;
            _logger = logger;
        }

        public async Task<AgentState> RunPipelineAsync(string targetProject, string sourceFile)
        {
            _logger.LogInformation($"[Pipeline] Démarrage du pipeline pour le fichier {sourceFile}.");
            int sessionId = await _dbContext.CreateSessionAsync(targetProject);
            var state = new AgentState 
            { 
                SessionId = sessionId, 
                TargetProjectPath = targetProject, 
                CurrentFilePath = sourceFile 
            };

            await ExecuteAgentAsync("Analyzer", state);
            await ExecuteAgentAsync("Decider", state);
            
            while (state.RetryCount < 3 && !state.IsFinished)
            {
                await ExecuteAgentAsync("Creator", state);
                await ExecuteAgentAsync("Verifier", state);

                if (state.ValidationErrors.Count > 0)
                {
                    _logger.LogWarning($"[Pipeline] Échec de validation (Tentative {state.RetryCount + 1}/3).");
                    state.RetryCount++;
                }
                else
                {
                    _logger.LogInformation("[Pipeline] Validation réussie.");
                    state.IsFinished = true;
                }
            }

            if (state.IsFinished)
            {
                await ExecuteAgentAsync("Exporter", state);
                _logger.LogInformation("[Pipeline] Pipeline terminé avec succès.");
            }
            else
            {
                _logger.LogError("[Pipeline] Échec du pipeline après toutes les tentatives.");
            }

            await _dbContext.UpdateSessionStateAsync(sessionId, JsonConvert.SerializeObject(state), state.IsFinished ? "Terminé" : "Erreur");
            return state;
        }

        private async Task ExecuteAgentAsync(string agentName, AgentState state)
        {
            foreach (var agent in _agents)
            {
                if (agent.Name == agentName)
                {
                    _logger.LogInformation($"[Pipeline] Exécution de l'agent : {agentName}");
                    await agent.ExecuteAsync(state);
                    return;
                }
            }
            _logger.LogError($"[Pipeline] Agent {agentName} non trouvé dans le conteneur DI.");
        }
    }
}
