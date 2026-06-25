using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class ExporterAgent : BaseAgent
    {
        public override string Name => "Exporter";

        public ExporterAgent(IDbContext dbContext, ILlmService llmService, ILogger<ExporterAgent> logger) 
            : base(dbContext, llmService, logger) { }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            _logger.LogInformation("Exportation du fichier de test...");
            string cleanCode = state.GeneratedTestCode;
            
            if (cleanCode.Contains("```csharp"))
            {
                cleanCode = cleanCode.Split("```csharp")[1].Split("```")[0];
            }
            else if (cleanCode.Contains("```"))
            {
                cleanCode = cleanCode.Split("```")[1].Split("```")[0];
            }

            string fileName = $"{state.Metadata?.ClassName}Tests.cs";
            string fullPath = Path.Combine(state.TargetProjectPath, fileName);
            string finalCode = cleanCode.Trim();

            _logger.LogInformation($"Écriture du fichier : {fullPath}");
            await File.WriteAllTextAsync(fullPath, finalCode);

            // Trace de l'action déterministe et de son résultat pour la « Communication agentique ».
            int byteCount = Encoding.UTF8.GetByteCount(finalCode);
            int lineCount = finalCode.Length == 0 ? 0 : finalCode.Split('\n').Length;
            await _dbContext.LogCommunicationAsync(
                state.SessionId, Name,
                $"Écriture du fichier {fileName}",
                $"Écriture du fichier de test sur le disque : {fullPath}",
                $"✅ Fichier écrit : {fullPath}\n{lineCount} ligne(s) • {byteCount} octet(s)");
        }
    }
}
