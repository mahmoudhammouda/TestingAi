using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public abstract class BaseAgent : IAgent
    {
        public abstract string Name { get; }
        protected readonly IDbContext _dbContext;
        protected readonly ILlmService _llmService;
        protected readonly ILogger _logger;

        protected BaseAgent(IDbContext dbContext, ILlmService llmService, ILogger logger)
        {
            _dbContext = dbContext;
            _llmService = llmService;
            _logger = logger;
        }

        public async Task ExecuteAsync(AgentState state)
        {
            _logger.LogInformation($"[{Name}] Début de l'exécution pour la session {state.SessionId}.");
            await _dbContext.LogCommunicationAsync(state.SessionId, Name, "Début de l'exécution.");
            
            try
            {
                await ProcessInternalAsync(state);
                _logger.LogInformation($"[{Name}] Fin de l'exécution avec succès.");
                await _dbContext.LogCommunicationAsync(state.SessionId, Name, "Fin de l'exécution avec succès.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{Name}] Erreur lors de l'exécution : {ex.Message}");
                await _dbContext.LogCommunicationAsync(state.SessionId, Name, $"Erreur : {ex.Message}");
                throw;
            }
        }

        protected abstract Task ProcessInternalAsync(AgentState state);
    }
}
