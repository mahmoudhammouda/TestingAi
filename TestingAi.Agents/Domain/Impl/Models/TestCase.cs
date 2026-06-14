namespace TestingAi.Agents.Domain.Impl.Models
{
    public enum TestStatus
    {
        Pending,
        Running,
        Green,
        Red,
        Ignored,
        EnAttenteDecision
    }

    public enum TestAction
    {
        None,
        FixTest,
        FixCode,
        Ignore
    }

    public class TestCase
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public string TestName { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public string TestFilePath { get; set; } = string.Empty;
        public string SourceFilePath { get; set; } = string.Empty;
        public TestStatus Status { get; set; } = TestStatus.Pending;
        public TestAction Action { get; set; } = TestAction.None;
        public string? ErrorMessage { get; set; }
        public string? FixApplied { get; set; }
        public int RetryCount { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? UpdatedAt { get; set; }
    }

    public class PipelineSettings
    {
        public bool HumanInterventionEnabled { get; set; } = false;
        public int MaxRetries { get; set; } = 3;
        public string PreferredProvider { get; set; } = "Gemini";
    }
}
