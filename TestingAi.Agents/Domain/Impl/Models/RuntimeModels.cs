using System;
using System.Collections.Generic;

namespace TestingAi.Agents.Domain.Impl.Models
{
    public class TestingSession
    {
        public int Id { get; set; }
        public string TargetProject { get; set; } = string.Empty;
        public string GlobalState { get; set; } = "{}"; // JSON sérialisé
        public string Status { get; set; } = "En_Cours";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public class AgentA2ACommunication
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string ActionSummary { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class AgentPrivateMemory
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string AgentName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty; // System, User, Assistant
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
