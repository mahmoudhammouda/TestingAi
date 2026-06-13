using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class AnalyzerAgent : BaseAgent
    {
        public override string Name => "Analyzer";

        public AnalyzerAgent(IDbContext dbContext, ILlmService llmService, ILogger<AnalyzerAgent> logger) 
            : base(dbContext, llmService, logger) { }

        protected override async Task ProcessInternalAsync(AgentState state)
        {
            _logger.LogInformation($"Analyse du fichier : {state.CurrentFilePath}");
            string sourceCode = await File.ReadAllTextAsync(state.CurrentFilePath);
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = await tree.GetRootAsync();

            var fileName = Path.GetFileNameWithoutExtension(state.CurrentFilePath);
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            
            var classDecl = classes.FirstOrDefault(c => c.Identifier.Text.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                           ?? classes.OrderByDescending(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Count()).FirstOrDefault();

            if (classDecl == null) throw new Exception("Aucune classe trouvée dans le fichier.");

            var metadata = new CodeMetadata
            {
                ClassName = classDecl.Identifier.Text,
                Namespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "Global",
                Dependencies = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.Name.ToString()).ToList(),
                Methods = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Select(m => new MethodMetadata
                    {
                        Name = m.Identifier.Text,
                        ReturnType = m.ReturnType.ToString(),
                        Parameters = m.ParameterList.Parameters.Select(p => new ParameterMetadata
                        {
                            Name = p.Identifier.Text,
                            Type = p.Type?.ToString() ?? "object"
                        }).ToList()
                    }).ToList()
            };

            var ctors = classDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
            if (ctors != null)
            {
                foreach (var param in ctors.ParameterList.Parameters)
                {
                    metadata.Dependencies.Add($"{param.Type} (Injecté)");
                }
            }

            state.Metadata = metadata;
            _logger.LogInformation($"Analyse terminée. Classe détectée : {metadata.ClassName}");
        }
    }
}
