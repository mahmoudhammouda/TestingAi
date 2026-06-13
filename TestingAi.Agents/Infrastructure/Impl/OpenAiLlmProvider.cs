using System;
using System.Threading.Tasks;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace TestingAi.Agents.Infrastructure.Impl
{
    public class OpenAiLlmProvider : ILlmProvider
    {
        public LlmProviderType ProviderType => LlmProviderType.OpenAi;
        private readonly string _apiKey;

        public OpenAiLlmProvider(string? apiKey = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "MISSING_KEY";
        }

        public async Task<LlmResponse> AskAsync(string prompt, string? systemMessage = null)
        {
            try
            {
                var client = new ChatClient("gpt-4o", _apiKey);
                var messages = new List<ChatMessage>();
                
                if (systemMessage != null)
                    messages.Add(new SystemChatMessage(systemMessage));
                
                messages.Add(new UserChatMessage(prompt));

                var response = await client.CompleteChatAsync(messages);

                return new LlmResponse { Content = response.Value.Content[0].Text, IsSuccess = true };
            }
            catch (Exception ex)
            {
                return new LlmResponse { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }
    }
}
