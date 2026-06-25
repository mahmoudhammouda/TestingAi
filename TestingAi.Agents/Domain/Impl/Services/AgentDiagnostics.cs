namespace TestingAi.Agents.Domain.Impl.Services
{
    // Utilitaires de journalisation : borne la taille des sorties de commande
    // persistées dans la timeline A2A (un `dotnet test` / `dotnet build` peut être
    // très verbeux). On conserve le début (contexte) et la fin (erreurs, récap).
    internal static class AgentDiagnostics
    {
        public static string Truncate(string? text, int maxLen = 6000)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Trim();
            if (text.Length <= maxLen) return text;
            int head = maxLen * 2 / 3;
            int tail = maxLen - head;
            return text.Substring(0, head)
                + $"\n\n… [sortie tronquée — {text.Length - maxLen} caractère(s) omis] …\n\n"
                + text.Substring(text.Length - tail);
        }
    }
}
