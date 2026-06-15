using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Infrastructure.Impl
{
    public class AnthropicLlmProvider : ILlmProvider
    {
        public LlmProviderType ProviderType => LlmProviderType.Anthropic;
        private readonly string _apiKey;
        private readonly string _model;
        private static readonly HttpClient _httpClient = new HttpClient();

        public AnthropicLlmProvider(string? apiKey = null, string? model = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "MISSING_KEY";
            _model = model ?? Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-3-5-sonnet-20241022";
        }

        public async Task<LlmResponse> AskAsync(string prompt, string? systemMessage = null)
        {
            try
            {
                var requestBody = new
                {
                    model = _model,
                    max_tokens = 4096,
                    system = systemMessage ?? string.Empty,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    dynamic result = JsonConvert.DeserializeObject(responseString)!;
                    string text = result.content[0].text;
                    return new LlmResponse { Content = text, IsSuccess = true };
                }

                return new LlmResponse { IsSuccess = false, ErrorMessage = $"API Error: {response.StatusCode} - {responseString}" };
            }
            catch (Exception ex)
            {
                return new LlmResponse { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }
    }
}
