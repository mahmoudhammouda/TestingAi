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

        // Champs utilisés par les agents historiques (analyse / génération)
        public string CurrentFilePath { get; set; } = string.Empty;
        public CodeMetadata? Metadata { get; set; }
        public string TestStrategy { get; set; } = string.Empty;
        public string GeneratedTestCode { get; set; } = string.Empty;
        public List<string> ValidationErrors { get; set; } = new();

        // Motif d'échec du pipeline (renseigné lorsque la session passe en "Erreur").
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
