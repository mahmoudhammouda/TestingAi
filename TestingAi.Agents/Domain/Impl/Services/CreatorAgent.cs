using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class CreatorAgent : BaseAgent
    {
        public override string Name => "Creator";

        public CreatorAgent(IDbContext dbContext, ILlmService llmService, ILogger<CreatorAgent> logger) 
            : base(dbContext, llmService, logger) { }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            _logger.LogInformation("Génération du code de test...");
            string prompt = $"Génère UNIQUEMENT le code C# (sans backticks ```) pour les tests unitaires xUnit de la classe {state.Metadata?.ClassName}.\n" +
                            $"Namespace original : {state.Metadata?.Namespace}\n" +
                            $"Méthodes à tester : \n" +
                            $"{string.Join("\n", state.Metadata?.Methods.Select(m => $"- {m.Name} ({string.Join(", ", m.Parameters.Select(p => p.Type + " " + p.Name))}) -> {m.ReturnType}"))}\n" +
                            $"Dépendances détectées : {string.Join(", ", state.Metadata?.Dependencies ?? new System.Collections.Generic.List<string>())}\n" +
                            $"Stratégie de test : {state.TestStrategy}\n" +
                            $"IMPORTANT : \n" +
                            $"- Ne redéfinis pas la classe {state.Metadata?.ClassName}.\n" +
                            $"- Utilise UNIQUEMENT les noms de méthodes fournis ci-dessus pour les mocks.\n" +
                            $"- NE PAS mettre de blocs de code Markdown (pas de ```csharp).\n" +
                            (state.ValidationErrors.Count > 0 ? $"FIXE CES ERREURS : {string.Join("\n", state.ValidationErrors)}" : "");

            string system = "Tu es un développeur senior .NET. Tu écris du code de test C# pur, sans aucune balise markdown, prêt à être sauvegardé dans un fichier .cs.";

            state.GeneratedTestCode = await _llmService.AskAsync(state.SessionId, Name, prompt, system);
            _logger.LogInformation("Code de test généré.");
        }
    }
}
