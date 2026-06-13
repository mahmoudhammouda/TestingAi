using System;
using System.Collections.Generic;

namespace TestingAi.Agents.Domain.Impl.Models
{
    public class AgentState
    {
        public int SessionId { get; set; }
        public string TargetProjectPath { get; set; } = string.Empty;
        public string CurrentFilePath { get; set; } = string.Empty;
        public CodeMetadata? Metadata { get; set; }
        public string TestStrategy { get; set; } = string.Empty;
        public string GeneratedTestCode { get; set; } = string.Empty;
        public List<string> ValidationErrors { get; set; } = new();
        public int RetryCount { get; set; } = 0;
        public bool IsFinished { get; set; } = false;
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
