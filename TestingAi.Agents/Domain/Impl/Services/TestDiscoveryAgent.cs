using System;
using System.Collections.Generic;
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
                .Where(NotBuildArtifact)
                .ToList();

            _logger.LogInformation("[Découverte] {Count} fichiers C# trouvés.", csFiles.Count);

            // Indexer les fichiers source (chemin + contenu) pour résoudre SourceFilePath — requis par l'action FixCode.
            var sourceIndex = BuildSourceIndex(state);

            foreach (var file in csFiles)
            {
                var content = await File.ReadAllTextAsync(file);
                var methods = ExtractTestMethods(content, file, state.SessionId, sourceIndex);

                foreach (var tc in methods)
                {
                    int id = await _db.UpsertTestCaseAsync(tc);
                    tc.Id = id;
                    state.TestCases.Add(tc);
                }
            }

            _logger.LogInformation("[Découverte] {Count} tests découverts.", state.TestCases.Count);

            // Trace de l'action déterministe et de son résultat pour la « Communication agentique ».
            var testList = string.Join("\n", state.TestCases.Select(t => $"  • {t.ClassName}.{t.MethodName}"));
            await _db.LogCommunicationAsync(
                state.SessionId, Name,
                $"Découverte : {state.TestCases.Count} test(s)",
                $"Analyse statique du projet de tests (scan récursif des fichiers *.cs)\nRépertoire : {state.TestProjectPath}",
                AgentDiagnostics.Truncate($"{csFiles.Count} fichier(s) C# analysé(s) • {state.TestCases.Count} test(s) découvert(s) :\n{testList}"));
        }

        private static bool NotBuildArtifact(string f) =>
            !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);

        private List<(string Path, string Content)> BuildSourceIndex(AgentState state)
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(state.SourceProjectPath) && Directory.Exists(state.SourceProjectPath))
                roots.Add(state.SourceProjectPath);
            // Repli : le code source peut résider dans le projet de tests lui-même.
            roots.Add(state.TestProjectPath);

            var index = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(NotBuildArtifact))
                {
                    if (!seen.Add(f)) continue;
                    try { index.Add((f, File.ReadAllText(f))); }
                    catch { /* fichier illisible ignoré */ }
                }
            }

            _logger.LogInformation("[Découverte] {Count} fichier(s) source indexé(s) pour FixCode.", index.Count);
            return index;
        }

        private List<TestCase> ExtractTestMethods(string content, string filePath, int sessionId, List<(string Path, string Content)> sourceIndex)
        {
            var results = new List<TestCase>();

            var nsMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
            var classMatch = Regex.Match(content, @"(?:public|internal)\s+(?:partial\s+)?class\s+(\w+)");
            var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "";
            var className = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);
            var fullClass = string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";

            var sourceFilePath = ResolveSourceFile(className, filePath, sourceIndex);

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
                    SourceFilePath = sourceFilePath,
                    Status = TestStatus.Pending,
                    Action = TestAction.None,
                    CreatedAt = DateTime.UtcNow.ToString("o")
                });
            }

            return results;
        }

        // Déduit le fichier source testé depuis le nom de la classe de test.
        // Ex. : "CalculatorTests" -> classe "Calculator" -> recherche `class Calculator` dans l'index source.
        private string ResolveSourceFile(string testClassName, string testFilePath, List<(string Path, string Content)> sourceIndex)
        {
            var candidate = StripTestAffix(testClassName);
            if (string.IsNullOrEmpty(candidate)) return "";

            var declRegex = new Regex($@"\b(?:class|record|struct|interface)\s+{Regex.Escape(candidate)}\b");

            foreach (var (path, text) in sourceIndex)
            {
                if (string.Equals(path, testFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (declRegex.IsMatch(text))
                {
                    _logger.LogInformation("[Découverte] Source de {Class} → {File}", candidate, path);
                    return path;
                }
            }

            _logger.LogInformation("[Découverte] Aucun fichier source trouvé pour {Class} (FixCode indisponible).", candidate);
            return "";
        }

        private static string StripTestAffix(string className)
        {
            foreach (var suffix in new[] { "Tests", "Test", "Specs", "Spec", "Fixture" })
            {
                if (className.EndsWith(suffix, StringComparison.Ordinal) && className.Length > suffix.Length)
                    return className.Substring(0, className.Length - suffix.Length);
            }
            if (className.StartsWith("Test", StringComparison.Ordinal) && className.Length > 4)
                return className.Substring(4);
            return className;
        }
    }
}
