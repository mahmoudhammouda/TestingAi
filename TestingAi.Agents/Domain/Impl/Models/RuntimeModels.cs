using System.Collections.Generic;

namespace TestingAi.Agents.Domain.Impl.Models
{
    public class TestingSession
    {
        public int Id { get; set; }
        public string TargetProject { get; set; } = string.Empty;
        public string SourceProject { get; set; } = string.Empty;
        public string GlobalState { get; set; } = "{}";
        public string Status { get; set; } = "En_Cours";
        public string CreatedAt { get; set; } = string.Empty;
        public string Metadata { get; set; } = string.Empty;
        public string TestStrategy { get; set; } = string.Empty;
    }

    public class AgentA2ACommunication
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string ActionSummary { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        // Commande lancée + résultat (agents déterministes / TestRunner) ; null pour
        // les simples événements de cycle de vie (Début/Succès/Erreur).
        public string? CommandText { get; set; }
        public string? CommandOutput { get; set; }
    }

    public class AgentPrivateMemory
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string AgentName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }

    public class CodeMetadata
    {
        public string ClassName { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public List<string> Dependencies { get; set; } = new();
        public List<MethodMetadata> Methods { get; set; } = new();
    }

    public class MethodMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string ReturnType { get; set; } = string.Empty;
        public List<ParameterMetadata> Parameters { get; set; } = new();
    }

    public class ParameterMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }
}
