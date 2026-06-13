using System;
using System.IO;
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
            
            _logger.LogInformation($"Écriture du fichier : {fullPath}");
            await File.WriteAllTextAsync(fullPath, cleanCode.Trim());
        }
    }
}
