using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    // Passe de vérification « spécification » des valeurs attendues.
    //
    // Le Creator demande au LLM d'écrire le test ET de calculer la valeur attendue
    // « de tête » : les LLM se trompent souvent sur ces calculs déterministes
    // (ex. "Xunit" inversé rendu "tinUx" au lieu de "tinuX"). Le Reviewer relit le
    // fichier généré et fiabilise CHAQUE valeur attendue, en raisonnant uniquement à
    // partir du COMPORTEMENT ATTENDU (nom de la méthode, paramètres, nom du test) et
    // SANS regarder l'implémentation réelle — ainsi un code source bogué reste
    // détectable par le test.
    public class ReviewerAgent : BaseAgent
    {
        public override string Name => "Reviewer";

        public ReviewerAgent(IDbContext dbContext, ILlmService llmService, ILogger<ReviewerAgent> logger)
            : base(dbContext, llmService, logger) { }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            if (string.IsNullOrWhiteSpace(state.GeneratedTestCode))
            {
                _logger.LogInformation("[Reviewer] Aucun code de test à vérifier — passe ignorée.");
                return;
            }

            var meta = state.Metadata;
            string className = string.IsNullOrWhiteSpace(meta?.ClassName) ? "Code" : meta!.ClassName;

            string methodsList = (meta?.Methods?.Count ?? 0) > 0
                ? string.Join("\n", meta!.Methods.Select(m =>
                    $"- {m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type + " " + p.Name))}) -> {m.ReturnType}"))
                : "(aucune méthode publique détectée)";

            string prompt =
                $"Objectif : Vérifier et fiabiliser les valeurs attendues des tests générés pour {className}\n\n" +
                "Tu es un relecteur méticuleux de tests unitaires xUnit. On te donne un fichier de tests C# DÉJÀ généré.\n" +
                "Ta mission : garantir que CHAQUE valeur attendue (expected) est correcte, SANS regarder une quelconque\n" +
                "implémentation de la classe testée. Tu raisonnes uniquement à partir du COMPORTEMENT ATTENDU décrit par\n" +
                "le nom de la méthode, ses paramètres et le nom du test. Ainsi, si le code source est bogué, le test pourra le révéler.\n\n" +
                $"Méthodes testées (signature et intention) :\n{methodsList}\n\n" +
                (string.IsNullOrWhiteSpace(state.TestStrategy) ? "" : $"Stratégie de test appliquée :\n{state.TestStrategy}\n\n") +
                "RÈGLES :\n" +
                "1. Pour CHAQUE assertion à valeur attendue codée en dur (Assert.Equal(expected, actual), etc.), recalcule pas à pas\n" +
                "   la valeur attendue à partir des ENTRÉES du test et du comportement attendu. Si ta valeur diffère du littéral\n" +
                "   présent, REMPLACE le littéral par la valeur correcte.\n" +
                "2. Quand le calcul exact est source d'erreur (inversion/manipulation de chaîne, maths non triviales, formatage de\n" +
                "   dates/nombres, ordre d'une collection…), NE devine PAS une valeur exacte : REMPLACE l'égalité exacte par une ou\n" +
                "   plusieurs assertions de PROPRIÉTÉ/INVARIANT qui ne nécessitent pas la valeur exacte (longueur préservée, double\n" +
                "   application qui redonne l'entrée, relation élément par élément avec l'entrée, appartenance à un ensemble, bornes…).\n" +
                "3. INTERDIT ABSOLU — ne crée JAMAIS d'assertion tautologique (toujours vraie) ni d'oracle circulaire :\n" +
                "   - N'utilise JAMAIS la méthode testée (ni la variable `result`/`actual` qu'elle a produite) pour CALCULER la valeur\n" +
                "     attendue. Ex. INTERDIT : `Assert.Equal(StringUtils.Reverse(input), result)`, `Assert.Equal(result, result)`.\n" +
                "   - Pas de `Assert.True(true)`, `Assert.NotNull(result)` seul, ni de propriété triviale toujours vraie.\n" +
                "   - Chaque assertion de propriété doit contraindre RÉELLEMENT la sortie À PARTIR DES ENTRÉES, de façon indépendante\n" +
                "     du résultat produit par la méthode (sinon un code bogué passerait quand même le test).\n" +
                "   - Dans le doute, garde l'égalité exacte avec une valeur recalculée à la main plutôt qu'une propriété faible.\n" +
                "4. Ne change NI les entrées des tests, NI les noms de tests, NI les appels de méthode. Tu ne touches QU'AUX assertions.\n" +
                "5. Garde un fichier C# complet et COMPILABLE : conserve les `using` existants et AJOUTE ceux nécessaires (ex. `using System.Linq;`).\n" +
                "6. Si tout est déjà correct, renvoie le fichier inchangé.\n\n" +
                $"Fichier de tests à vérifier :\n{state.GeneratedTestCode}\n\n" +
                "Retourne UNIQUEMENT le fichier C# complet corrigé, sans Markdown (pas de triples backticks), sans explication, sans texte avant ou après.";

            string system = "Tu es un expert .NET en tests unitaires xUnit, rigoureux sur l'exactitude des valeurs attendues. " +
                            "Tu renvoies uniquement du code C# pur, complet et compilable, sans aucune balise Markdown.";

            var reviewed = await _llmService.AskAsync(state.SessionId, Name, prompt, system);
            if (!string.IsNullOrWhiteSpace(reviewed))
            {
                state.GeneratedTestCode = reviewed;
                _logger.LogInformation("[Reviewer] Valeurs attendues vérifiées ({Len} caractères).", reviewed.Length);
            }
            else
            {
                _logger.LogInformation("[Reviewer] Réponse vide — version générée conservée telle quelle.");
            }
        }
    }
}
