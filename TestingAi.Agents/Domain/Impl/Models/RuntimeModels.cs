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
    }

    public class AgentA2ACommunication
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string ActionSummary { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
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
}
