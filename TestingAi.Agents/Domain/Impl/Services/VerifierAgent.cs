using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class VerifierAgent : BaseAgent
    {
        private readonly IProcessRunner _processRunner;
        public override string Name => "Verifier";

        public VerifierAgent(IDbContext dbContext, ILlmService llmService, IProcessRunner processRunner, ILogger<VerifierAgent> logger) 
            : base(dbContext, llmService, logger) 
        {
            _processRunner = processRunner;
        }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            _logger.LogInformation($"Vérification par build du projet : {state.TargetProjectPath}");
            var result = await _processRunner.RunAsync(state.TargetProjectPath, "dotnet", "build");

            state.ValidationErrors.Clear();
            if (result.ExitCode != 0)
            {
                _logger.LogWarning("Échec du build. Métadonnées d'erreurs extraites.");
                state.ValidationErrors.Add(result.Error);
                state.ValidationErrors.Add(result.Output);
            }
            else
            {
                _logger.LogInformation("Build réussi.");
            }
        }
    }
}
