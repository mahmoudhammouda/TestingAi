using System;
using System.Collections.Generic;
using System.Linq;

namespace TestingAi.Agents.Domain.Impl.Services
{
    /// <summary>
    /// Classe les sorties d'échec de build (dotnet build) afin de décider si l'échec est
    /// imputable à l'ACCESSIBILITÉ du code SOURCE (et non à de simples bugs dans le code de
    /// test généré). C'est ce diagnostic qui conditionne le déclenchement de l'adaptation
    /// de la source : on ne touche jamais à la source pour une erreur purement côté test.
    /// </summary>
    public static class BuildFailureClassifier
    {
        // Marqueurs compilateur indiquant qu'un symbole de la source est inaccessible depuis
        // le projet de tests (classe 'internal', 'Program' à instructions de haut niveau, …).
        // Le correctif « ami d'assembly » (InternalsVisibleTo) lève précisément ce blocage
        // sans modifier la logique métier de la source.
        private static readonly string[] AccessibilityMarkers =
        {
            "CS0122",                                    // 'X' is inaccessible due to its protection level
            "inaccessible due to its protection level",  // libellé en clair (selon la culture du compilateur)
        };

        /// <summary>
        /// Vrai si la sortie de build contient au moins un blocage d'accessibilité de la source.
        /// </summary>
        public static bool ImplicatesSourceAccessibility(IEnumerable<string>? buildOutput)
        {
            if (buildOutput == null) return false;
            var text = string.Join("\n", buildOutput.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(text)) return false;
            return AccessibilityMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
    }
}
