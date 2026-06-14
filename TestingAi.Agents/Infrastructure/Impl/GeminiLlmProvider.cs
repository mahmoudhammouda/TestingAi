using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Infrastructure.Impl
{
    public class GeminiLlmProvider : ILlmProvider
    {
        public LlmProviderType ProviderType => LlmProviderType.Gemini;
        private readonly string _apiKey;
        private static readonly HttpClient _httpClient = new HttpClient();

        public GeminiLlmProvider(string? apiKey = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "MISSING_KEY";
        }

        public async Task<LlmResponse> AskAsync(string prompt, string? systemMessage = null)
        {
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={_apiKey}";

                var requestBody = new
                {
                    contents = new[]
                    {
                        new { role = "user", parts = new[] { new { text = (systemMessage != null ? $"{systemMessage}\n\n{prompt}" : prompt) } } }
                    }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    dynamic result = JsonConvert.DeserializeObject(responseString)!;
                    string text = result.candidates[0].content.parts[0].text;
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
