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

            var meta = state.Metadata;
            var ns = meta?.Namespace ?? "";
            bool hasRealNs = !string.IsNullOrWhiteSpace(ns) && ns != "Global";
            string usingSource = hasRealNs ? $"using {ns};\n" : "";
            string testNamespace = hasRealNs ? $"{ns}.Tests" : "GeneratedTests";
            string className = string.IsNullOrWhiteSpace(meta?.ClassName) ? "Code" : meta!.ClassName;

            string methodsList = (meta?.Methods?.Count ?? 0) > 0
                ? string.Join("\n", meta!.Methods.Select(m =>
                    $"- {m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type + " " + p.Name))}) -> {m.ReturnType}"))
                : "(aucune méthode publique détectée — teste le comportement observable de la classe)";

            string prompt =
                $"Génère un FICHIER C# COMPLET et COMPILABLE de tests unitaires xUnit pour la classe `{className}`.\n\n" +
                $"Structure EXACTE attendue au début du fichier :\n" +
                $"using Xunit;\n{usingSource}\nnamespace {testNamespace};\n\n" +
                $"public class {className}Tests\n{{\n    // vos [Fact] / [Theory] ici\n}}\n\n" +
                $"Méthodes publiques à tester :\n{methodsList}\n\n" +
                (string.IsNullOrWhiteSpace(state.TestStrategy) ? "" : $"Stratégie de test à appliquer :\n{state.TestStrategy}\n\n") +
                $"RÈGLES STRICTES :\n" +
                $"- Retourne UNIQUEMENT du code C# brut, AUCUN bloc Markdown (pas de triples backticks).\n" +
                $"- NE REDÉFINIS PAS la classe `{className}` : elle provient du projet source référencé.\n" +
                $"- N'appelle que des membres réellement listés ci-dessus.\n" +
                $"- Chaque test porte [Fact] ou [Theory] et contient de vraies assertions Assert.*.\n" +
                $"- Le fichier doit compiler tel quel." +
                (state.ValidationErrors.Count > 0
                    ? $"\n\nLe build PRÉCÉDENT a ÉCHOUÉ. Corrige précisément ces erreurs de compilation :\n{string.Join("\n", state.ValidationErrors)}"
                    : "");

            string system = "Tu es un développeur senior .NET expert en tests unitaires xUnit. " +
                            "Tu écris du code C# pur, complet et compilable, sans aucune balise Markdown, " +
                            "prêt à être enregistré tel quel dans un fichier .cs.";

            state.GeneratedTestCode = await _llmService.AskAsync(state.SessionId, Name, prompt, system);
            _logger.LogInformation("Code de test généré ({Len} caractères).", state.GeneratedTestCode?.Length ?? 0);
        }
    }
}
