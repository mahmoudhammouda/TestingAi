namespace TestingAi.Agents.Domain.Intf.Models
{
    public enum LlmProviderType
    {
        Gemini,
        OpenAi,
        Anthropic, // Claude
        Copilot    // Généralement via le SDK OpenAi
    }

    public class LlmResponse
    {
        public string Content { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
