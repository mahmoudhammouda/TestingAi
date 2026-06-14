using System.Collections.Generic;

namespace TestingAi.Agents.Domain.Impl.Models
{
    public class AgentState
    {
        public int SessionId { get; set; }
        public string TestProjectPath { get; set; } = string.Empty;
        public string SourceProjectPath { get; set; } = string.Empty;
        public string TargetProjectPath { get; set; } = string.Empty;
        public List<TestCase> TestCases { get; set; } = new();
        public PipelineSettings Settings { get; set; } = new();
        public string PipelineStatus { get; set; } = "En_Cours";
        public bool IsFinished { get; set; } = false;
    }
}
