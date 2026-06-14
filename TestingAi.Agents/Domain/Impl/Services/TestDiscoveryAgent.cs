using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class TestDiscoveryAgent : IAgent
    {
        public string Name => "TestDiscoveryAgent";
        private readonly IDbContext _db;
        private readonly ILogger<TestDiscoveryAgent> _logger;

        public TestDiscoveryAgent(IDbContext db, ILogger<TestDiscoveryAgent> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task ExecuteAsync(AgentState state)
        {
            _logger.LogInformation("[Découverte] Analyse du projet : {Path}", state.TestProjectPath);

            if (!Directory.Exists(state.TestProjectPath))
                throw new DirectoryNotFoundException($"Projet de tests introuvable : {state.TestProjectPath}");

            var csFiles = Directory.GetFiles(state.TestProjectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToList();

            _logger.LogInformation("[Découverte] {Count} fichiers C# trouvés.", csFiles.Count);

            foreach (var file in csFiles)
            {
                var content = await File.ReadAllTextAsync(file);
                var methods = ExtractTestMethods(content, file, state.SessionId);

                foreach (var tc in methods)
                {
                    int id = await _db.UpsertTestCaseAsync(tc);
                    tc.Id = id;
                    state.TestCases.Add(tc);
                }
            }

            _logger.LogInformation("[Découverte] {Count} tests découverts.", state.TestCases.Count);
        }

        private System.Collections.Generic.List<TestCase> ExtractTestMethods(string content, string filePath, int sessionId)
        {
            var results = new System.Collections.Generic.List<TestCase>();

            // Extraire le namespace/classe
            var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
            var classMatch = Regex.Match(content, @"(?:public|internal)\s+(?:partial\s+)?class\s+(\w+)");
            var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "";
            var className = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);
            var fullClass = string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";

            // Détecter les attributs de test (xUnit, NUnit, MSTest)
            var testAttrPattern = @"\[(?:Fact|Theory|Test|TestMethod|TestCase)[^\]]*\]";
            var methodPattern = new Regex(
                testAttrPattern + @"[\s\S]*?" +
                @"(?:public|private|protected|internal)\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(",
                RegexOptions.Multiline);

            foreach (Match m in methodPattern.Matches(content))
            {
                var methodName = m.Groups[1].Value;
                results.Add(new TestCase
                {
                    SessionId = sessionId,
                    TestName = $"{fullClass}.{methodName}",
                    ClassName = fullClass,
                    MethodName = methodName,
                    TestFilePath = filePath,
                    SourceFilePath = "",
                    Status = TestStatus.Pending,
                    Action = TestAction.None,
                    CreatedAt = DateTime.UtcNow.ToString("o")
                });
            }

            return results;
        }
    }
}
