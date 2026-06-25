using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Newtonsoft.Json;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class DeciderAgent : BaseAgent
    {
        public override string Name => "Decider";

        public DeciderAgent(IDbContext dbContext, ILlmService llmService, ILogger<DeciderAgent> logger) 
            : base(dbContext, llmService, logger) { }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            _logger.LogInformation("Définition de la stratégie de test...");
            string prompt = $"Analyse les métadonnées suivantes pour la classe {state.Metadata?.ClassName} et définit une stratégie de test unitaire complète (cas nominaux, limites, erreurs).\n" +
                            $"Métadonnées : {JsonConvert.SerializeObject(state.Metadata)}";

            string system = "Tu es un expert en test unitaires .NET. Réponds en Français. Propose une liste de scénarios de tests à implémenter.";

            state.TestStrategy = await _llmService.AskAsync(state.SessionId, Name, prompt, system);
            _logger.LogInformation("Stratégie de test définie avec succès.");
        }
    }
}
