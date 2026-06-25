using System.Collections.Generic;
using System.Linq;
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
            var result = await _processRunner.RunAsync(state.TargetProjectPath, "dotnet", "build", timeoutSeconds: 120);

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

            // Trace de la commande lancée et de son résultat pour la « Communication agentique ».
            var combined = string.Join("\n",
                new[] { result.Output, result.Error }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var verdict = result.ExitCode == 0
                ? "✅ Build réussi — le code des tests compile."
                : $"❌ Échec du build (code {result.ExitCode}) — erreurs de compilation.";
            await _dbContext.LogCommunicationAsync(
                state.SessionId, Name,
                $"Commande dotnet build (code {result.ExitCode})",
                $"dotnet build   (répertoire : {state.TargetProjectPath})",
                $"Code de sortie : {result.ExitCode}\n{verdict}\n\n{AgentDiagnostics.Truncate(combined)}");
        }
    }
}
