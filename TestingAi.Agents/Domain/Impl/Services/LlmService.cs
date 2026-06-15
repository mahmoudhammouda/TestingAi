using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Intf.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class LlmService : ILlmService
    {
        private readonly IEnumerable<ILlmProvider> _providers;
        private readonly IDbContext _dbContext;
        private readonly ILogger<LlmService> _logger;

        public LlmService(IEnumerable<ILlmProvider> providers, IDbContext dbContext, ILogger<LlmService> logger)
        {
            _providers = providers;
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<string> AskAsync(int sessionId, string agentName, string prompt, string? systemMessage = null, LlmProviderType? preferredProvider = null)
        {
            LlmProviderType providerType;
            if (preferredProvider.HasValue)
            {
                providerType = preferredProvider.Value;
            }
            else
            {
                var settings = await _dbContext.GetSettingsAsync();
                providerType = Enum.TryParse<LlmProviderType>(settings.PreferredProvider, true, out var p)
                    ? p : LlmProviderType.Gemini;
            }

            var provider = _providers.FirstOrDefault(p => p.ProviderType == providerType) 
                           ?? _providers.First();

            _logger.LogDebug($"[LLM] Appel au provider {providerType} pour l'agent {agentName}.");

            await _dbContext.SavePrivateMemoryAsync(sessionId, agentName, "User", prompt);
            if (systemMessage != null)
                await _dbContext.SavePrivateMemoryAsync(sessionId, agentName, "System", systemMessage);

            var response = await provider.AskAsync(prompt, systemMessage);

            if (response.IsSuccess)
            {
                _logger.LogDebug($"[LLM] R�ponse re�ue de {providerType}.");
                await _dbContext.SavePrivateMemoryAsync(sessionId, agentName, "Assistant", response.Content);
                return response.Content;
            }

            _logger.LogError($"[LLM] Erreur ({providerType}) : {response.ErrorMessage}");
            throw new Exception($"Erreur LLM ({providerType}): {response.ErrorMessage}");
        }
    }
}
