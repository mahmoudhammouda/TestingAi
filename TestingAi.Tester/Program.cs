using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Intf.Services;
using TestingAi.Agents.Domain.Impl.Services;
using TestingAi.Agents.Infrastructure.Impl;
using TestingAi.Agents.Domain.Intf.Models;
using System.Collections.Generic;
using System.IO;

namespace TestingAi.Tester
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== TestingAi : Demonstration du Pipeline Agents ===");

            if (args.Length < 2)
            {
                Console.WriteLine("Usage: dotnet run -- <CheminVersFichierCS> <RepertoireCibleProjet>");
                return;
            }

            string sourceFile = args[0];
            string targetProject = args[1];
            string logFilePath = Path.Combine("C:\\Work\\Git\\TestingAi\\TestingAi.Tester\\logs", $"run_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            var services = new ServiceCollection();
            
            // Configuration du Logging (Console + Fichier)
            services.AddLogging(builder => 
            {
                builder.AddConsole();
                builder.AddFile(logFilePath);
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Infrastructure
            services.AddSingleton<IDbContext>(new DbContext("C:\\Work\\Git\\TestingAi\\TestingAi.Agents\\Data\\TestingAi.Agents.db"));
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<ILlmProvider, GeminiLlmProvider>();
            services.AddSingleton<ILlmProvider, OpenAiLlmProvider>();
            
            // Services Domaine
            services.AddSingleton<ILlmService, LlmService>();
            
            // Agents
            services.AddSingleton<IAgent, AnalyzerAgent>();
            services.AddSingleton<IAgent, DeciderAgent>();
            services.AddSingleton<IAgent, CreatorAgent>();
            services.AddSingleton<IAgent, VerifierAgent>();
            services.AddSingleton<IAgent, ExporterAgent>();

            // Orchestrateur
            services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

            var serviceProvider = services.BuildServiceProvider();
            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            
            try 
            {
                logger.LogInformation($"[START] Lancement du pipeline pour : {sourceFile}");
                var finalState = await orchestrator.RunPipelineAsync(targetProject, sourceFile);

                logger.LogInformation("=== RESULTAT DU PIPELINE ===");
                logger.LogInformation($"Session ID : {finalState.SessionId}");
                logger.LogInformation($"Statut Final : {(finalState.IsFinished ? "SUCCES" : "ECHEC")}");
                
                if (finalState.IsFinished)
                {
                    Console.WriteLine("\n[SUCCES] Test genere et exporte.");
                    Console.WriteLine($"Logs disponibles dans : {logFilePath}");
                }
            }
            catch (Exception ex)
            {
                logger.LogCritical($"[CRITICAL] : {ex.Message}");
            }
        }
    }
}
