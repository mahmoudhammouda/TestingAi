using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Domain.Impl.Services
{
    public class AgentOrchestrator : IAgentOrchestrator
    {
        private readonly IDbContext _db;

        // Phase de GÉNÉRATION (agents BaseAgent — ils journalisent eux-mêmes).
        private readonly AnalyzerAgent _analyzer;
        private readonly DeciderAgent _decider;
        private readonly CreatorAgent _creator;
        private readonly ExporterAgent _exporter;
        private readonly VerifierAgent _verifier;
        private readonly ReviewerAgent _reviewer;
        private readonly SourceAdapterAgent _sourceAdapter;

        // Phase de VÉRIFICATION (agents IAgent — journalisés par l'orchestrateur).
        private readonly TestDiscoveryAgent _discovery;
        private readonly TestRunnerAgent _runner;
        private readonly TestDecisionAgent _decision;
        private readonly TestFixerAgent _fixer;

        private readonly ILogger<AgentOrchestrator> _logger;

        // Sérialise les exécutions visant le même projet de tests (évite les courses sur le disque/DB).
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new();
        private static SemaphoreSlim LockFor(string key) =>
            _pathLocks.GetOrAdd(key ?? string.Empty, _ => new SemaphoreSlim(1, 1));

        public AgentOrchestrator(
            IDbContext db,
            AnalyzerAgent analyzer,
            DeciderAgent decider,
            CreatorAgent creator,
            ExporterAgent exporter,
            VerifierAgent verifier,
            ReviewerAgent reviewer,
            SourceAdapterAgent sourceAdapter,
            TestDiscoveryAgent discovery,
            TestRunnerAgent runner,
            TestDecisionAgent decision,
            TestFixerAgent fixer,
            ILogger<AgentOrchestrator> logger)
        {
            _db = db;
            _analyzer = analyzer;
            _decider = decider;
            _creator = creator;
            _exporter = exporter;
            _verifier = verifier;
            _reviewer = reviewer;
            _sourceAdapter = sourceAdapter;
            _discovery = discovery;
            _runner = runner;
            _decision = decision;
            _fixer = fixer;
            _logger = logger;
        }

        public async Task<AgentState> RunPipelineAsync(string testProjectPath, string sourceProjectPath, string? generateFromSourceFile = null)
        {
            _logger.LogInformation("[Orchestrateur] Démarrage du pipeline pour : {Path}", testProjectPath);

            var gate = LockFor(testProjectPath);
            await gate.WaitAsync();
            try
            {
                var settings = await _db.GetSettingsAsync();
                int sessionId = await _db.CreateSessionAsync(testProjectPath, sourceProjectPath);

                var state = new AgentState
                {
                    SessionId = sessionId,
                    TestProjectPath = testProjectPath,
                    SourceProjectPath = sourceProjectPath,
                    TargetProjectPath = testProjectPath,
                    Settings = settings,
                    PipelineStatus = "En_Cours"
                };

                await ExecuteFullPipelineAsync(state, generateFromSourceFile);
                return state;
            }
            finally
            {
                gate.Release();
            }
        }

        // Exécute le pipeline pour une session DÉJÀ créée (import de dossier), en éventail
        // sur plusieurs fichiers source : phase de génération répétée par fichier, puis une
        // seule passe découverte → exécution → décision → correction sur l'ensemble des tests.
        public async Task<AgentState> RunPipelineForSessionAsync(
            int sessionId, string testProjectPath, string sourceProjectPath, IReadOnlyList<string> sourceFiles)
        {
            _logger.LogInformation(
                "[Orchestrateur] Démarrage du pipeline multi-fichiers ({N} fichier(s)) pour la session {Id}.",
                sourceFiles.Count, sessionId);

            var gate = LockFor(testProjectPath);
            await gate.WaitAsync();
            try
            {
                var settings = await _db.GetSettingsAsync();
                var state = new AgentState
                {
                    SessionId = sessionId,
                    TestProjectPath = testProjectPath,
                    SourceProjectPath = sourceProjectPath,
                    TargetProjectPath = testProjectPath,
                    Settings = settings,
                    PipelineStatus = "En_Cours",
                    FilesTotal = sourceFiles.Count
                };

                await ExecuteMultiFilePipelineAsync(state, sourceFiles);
                return state;
            }
            finally
            {
                gate.Release();
            }
        }

        // Corps du pipeline multi-fichiers. Chaque fichier passe par la phase de génération
        // de façon ISOLÉE (état de génération réinitialisé) afin d'éviter toute fuite d'un
        // fichier à l'autre. Un échec de génération est enregistré puis on poursuit avec les
        // fichiers suivants (le fichier de test défaillant est retiré du disque pour ne pas
        // casser le build — le Verifier compile le projet de tests ENTIER). Enfin, une seule
        // passe de validation/correction couvre l'ensemble des tests générés.
        private async Task ExecuteMultiFilePipelineAsync(AgentState state, IReadOnlyList<string> sourceFiles)
        {
            try
            {
                var perFileArtifacts = new List<object>();
                var strategyParts = new List<string>();
                var failedFiles = new List<string>();
                int total = sourceFiles.Count;

                for (int i = 0; i < total; i++)
                {
                    var file = sourceFiles[i];
                    var fileName = Path.GetFileName(file);

                    // Progression visible via le sondage (GlobalState) : « Fichier i+1/total ».
                    state.FilesDone = i;
                    state.CurrentFileName = fileName;
                    await UpdateProgressAsync(state);
                    await _db.LogCommunicationAsync(state.SessionId, "Orchestrateur",
                        $"▶ Génération du fichier {i + 1}/{total} : {fileName}");

                    // Isolation : repart d'un état de génération propre pour ce fichier.
                    state.CurrentFilePath = string.Empty;
                    state.Metadata = null;
                    state.TestStrategy = string.Empty;
                    state.GeneratedTestCode = string.Empty;
                    state.ValidationErrors = new List<string>();

                    try
                    {
                        await RunGenerationPhaseAsync(state, file);
                        perFileArtifacts.Add(new
                        {
                            File = fileName,
                            ClassName = state.Metadata?.ClassName ?? string.Empty,
                            Metadata = state.Metadata,
                            Strategy = state.TestStrategy ?? string.Empty
                        });
                        strategyParts.Add(
                            $"### {fileName}\n" +
                            (string.IsNullOrWhiteSpace(state.TestStrategy) ? "(aucune stratégie)" : state.TestStrategy));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Orchestrateur] Génération en échec pour {File} — poursuite.", fileName);
                        failedFiles.Add(fileName);
                        RemoveGeneratedTestFile(state);
                        await _db.LogCommunicationAsync(state.SessionId, "Orchestrateur",
                            $"Erreur de génération pour {fileName} : {ex.Message}");
                        perFileArtifacts.Add(new { File = fileName, Error = ex.Message });
                    }
                }

                state.FilesDone = total;
                state.CurrentFileName = string.Empty;
                await UpdateProgressAsync(state);

                // Persiste les artefacts de génération par fichier (tableau JSON) pour l'affichage.
                try
                {
                    await _db.UpdateSessionGenerationAsync(
                        state.SessionId,
                        JsonConvert.SerializeObject(perFileArtifacts),
                        string.Join("\n\n", strategyParts));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Orchestrateur] Persistance des artefacts multi-fichiers impossible.");
                }

                // ── Passe unique de validation/correction sur l'ensemble des tests ──
                await RunAgentAsync(_discovery, state);

                if (state.TestCases.Count == 0)
                {
                    _logger.LogWarning("[Orchestrateur] Aucun test découvert (multi-fichiers).");
                    state.PipelineStatus = failedFiles.Count > 0 ? "EchecPartiel" : "Terminé";
                    state.IsFinished = true;
                    await SaveSessionState(state, state.PipelineStatus);
                    return;
                }

                await RunAgentAsync(_runner, state);

                int redCount = state.TestCases.Count(t => t.Status == TestStatus.Red);
                if (redCount == 0)
                {
                    state.PipelineStatus = failedFiles.Count > 0 ? "EchecPartiel" : "Terminé";
                    state.IsFinished = true;
                    await SaveSessionState(state, state.PipelineStatus);
                    return;
                }

                await RunAgentAsync(_decision, state);

                if (state.Settings.HumanInterventionEnabled && state.PipelineStatus == "EnAttenteDecision")
                {
                    _logger.LogInformation("[Orchestrateur] Pipeline multi-fichiers suspendu — décisions humaines.");
                    await SaveSessionState(state, "EnAttenteDecision");
                    return;
                }

                await RunFixAndVerifyLoopAsync(state);

                // Des fichiers ont échoué à la génération → succès PARTIEL même si tous les
                // tests générés sont verts.
                if (failedFiles.Count > 0 && state.PipelineStatus == "Terminé")
                {
                    state.PipelineStatus = "EchecPartiel";
                    await SaveSessionState(state, "EchecPartiel");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrateur] Pipeline multi-fichiers en échec — session {Id} marquée Erreur.", state.SessionId);
                state.IsFinished = true;
                state.PipelineStatus = "Erreur";
                state.ErrorMessage = ex.Message;
                try { await SaveSessionState(state, "Erreur"); } catch { /* persistance best-effort */ }
                throw;
            }
        }

        // Retire du projet de tests le fichier généré pour le fichier source courant (le cas
        // échéant), identifié par le nom de classe analysé — best-effort.
        private void RemoveGeneratedTestFile(AgentState state)
        {
            var className = state.Metadata?.ClassName;
            if (string.IsNullOrWhiteSpace(className)) return;
            try
            {
                var path = Path.Combine(state.TargetProjectPath, $"{className}Tests.cs");
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrateur] Suppression du fichier de test défaillant impossible.");
            }
        }

        // Relance complète d'une session existante (par ex. après une erreur) :
        // repart d'un état propre puis ré-exécute toute la chaîne — génération comprise —
        // en réutilisant le MÊME identifiant de session.
        public async Task<AgentState> RerunPipelineAsync(int sessionId)
        {
            var session = await _db.GetSessionAsync(sessionId);
            if (session == null) throw new Exception($"Session {sessionId} introuvable.");

            _logger.LogInformation("[Orchestrateur] Relance complète de la session {Id}", sessionId);

            var gate = LockFor(session.TargetProject);
            await gate.WaitAsync();
            try
            {
                // Retrouve TOUS les fichiers source à régénérer (hors bin/obj) — indispensable
                // pour relancer une session issue d'un import de dossier (multi-fichiers), et
                // non plus seulement le premier .cs rencontré.
                var sourceFiles = new List<string>();
                if (Directory.Exists(session.SourceProject))
                {
                    var sep = Path.DirectorySeparatorChar;
                    sourceFiles = Directory
                        .EnumerateFiles(session.SourceProject, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .ToList();
                }

                if (sourceFiles.Count == 0)
                    throw new Exception(
                        "Code source introuvable : le dossier de travail de la session a probablement été supprimé. " +
                        "Créez une nouvelle session à partir du code.");

                // Repart d'un état propre (supprime tests/timeline/mémoire de la tentative précédente).
                await _db.ResetSessionForRerunAsync(sessionId);

                var settings = await _db.GetSettingsAsync();
                var state = new AgentState
                {
                    SessionId = sessionId,
                    TestProjectPath = session.TargetProject,
                    SourceProjectPath = session.SourceProject,
                    TargetProjectPath = session.TargetProject,
                    Settings = settings,
                    PipelineStatus = "En_Cours",
                    FilesTotal = sourceFiles.Count
                };

                if (sourceFiles.Count == 1)
                    await ExecuteFullPipelineAsync(state, sourceFiles[0]);
                else
                    await ExecuteMultiFilePipelineAsync(state, sourceFiles);
                return state;
            }
            finally
            {
                gate.Release();
            }
        }

        // Corps commun du pipeline (génération → découverte → exécution → décision → correction).
        // Marque la session "Erreur" et journalise le motif en cas d'échec.
        private async Task ExecuteFullPipelineAsync(AgentState state, string? generateFromSourceFile)
        {
            try
            {
                // Étape 0 : GÉNÉRATION (uniquement si une source à générer est fournie)
                if (!string.IsNullOrWhiteSpace(generateFromSourceFile) && File.Exists(generateFromSourceFile))
                {
                    await RunGenerationPhaseAsync(state, generateFromSourceFile!);
                }

                // Étape 1 : Découverte
                await RunAgentAsync(_discovery, state);

                if (state.TestCases.Count == 0)
                {
                    _logger.LogWarning("[Orchestrateur] Aucun test découvert.");
                    state.PipelineStatus = "Terminé";
                    state.IsFinished = true;
                    await SaveSessionState(state);
                    return;
                }

                // Étape 2 : Exécution
                await RunAgentAsync(_runner, state);

                int redCount = state.TestCases.Count(t => t.Status == TestStatus.Red);
                if (redCount == 0)
                {
                    _logger.LogInformation("[Orchestrateur] Tous les tests sont verts !");
                    state.PipelineStatus = "Terminé";
                    state.IsFinished = true;
                    await SaveSessionState(state);
                    return;
                }

                // Étape 3 : Décision
                await RunAgentAsync(_decision, state);

                // Mode humain : suspendre ici
                if (state.Settings.HumanInterventionEnabled && state.PipelineStatus == "EnAttenteDecision")
                {
                    _logger.LogInformation("[Orchestrateur] Pipeline suspendu — en attente des décisions humaines.");
                    await SaveSessionState(state, "EnAttenteDecision");
                    return;
                }

                // Mode auto : continuer
                await RunFixAndVerifyLoopAsync(state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrateur] Pipeline en échec — session {Id} marquée Erreur.", state.SessionId);
                state.IsFinished = true;
                state.PipelineStatus = "Erreur";
                state.ErrorMessage = ex.Message;
                try { await SaveSessionState(state, "Erreur"); } catch { /* persistance best-effort */ }
                throw;
            }
        }

        public async Task<AgentState> ResumePipelineAsync(int sessionId)
        {
            _logger.LogInformation("[Orchestrateur] Reprise du pipeline pour la session {Id}", sessionId);

            var session = await _db.GetSessionAsync(sessionId);
            if (session == null) throw new Exception($"Session {sessionId} introuvable.");

            var gate = LockFor(session.TargetProject);
            await gate.WaitAsync();
            try
            {
                var testCases = (await _db.GetTestCasesAsync(sessionId)).ToList();
                var settings = await _db.GetSettingsAsync();

                var state = new AgentState
                {
                    SessionId = sessionId,
                    TestProjectPath = session.TargetProject,
                    SourceProjectPath = session.SourceProject,
                    TargetProjectPath = session.TargetProject,
                    TestCases = testCases,
                    Settings = settings,
                    PipelineStatus = "En_Cours"
                };

                var pending = state.TestCases.Where(t => t.Status == TestStatus.EnAttenteDecision).ToList();
                if (pending.Count > 0)
                    throw new Exception($"{pending.Count} test(s) sont encore en attente de décision humaine.");

                await RunFixAndVerifyLoopAsync(state);
                return state;
            }
            finally
            {
                gate.Release();
            }
        }

        // ── Phase de génération : Analyzer → Decider → (Creator → Exporter → Verifier)* ──
        private async Task RunGenerationPhaseAsync(AgentState state, string sourceFile)
        {
            _logger.LogInformation("[Orchestrateur] Phase de génération à partir de : {File}", sourceFile);
            state.CurrentFilePath = sourceFile;

            // 1. Analyse statique (Roslyn) du code source.
            await RunGenerationAgentAsync(_analyzer, state);
            // 2. Stratégie de test (LLM) à partir des métadonnées.
            await RunGenerationAgentAsync(_decider, state);

            // 3. Génération + export + vérification du build, avec auto-correction si le build échoue.
            const int maxGenAttempts = 2;
            await RunCreatorVerifyLoopAsync(state, maxGenAttempts);

            // 3 bis. Si le build reste bloqué par l'ACCESSIBILITÉ du code source (et non par de
            //        simples bugs du test généré), on adapte la source (InternalsVisibleTo) pour
            //        débloquer, puis on régénère une fois. Sans effet si l'échec est purement
            //        côté test : la source reste alors strictement inchangée.
            if (state.ValidationErrors.Count > 0)
            {
                await TryAdaptSourceAndRegenerateAsync(state, maxGenAttempts);
            }

            // 4. Passe de vérification « spécification » des valeurs attendues (best-effort) :
            //    ne tourne que si le build est vert et ne doit jamais dégrader le résultat.
            if (state.ValidationErrors.Count == 0)
            {
                await RunAssertionReviewAsync(state);
            }

            // Persiste les artefacts de génération pour la visualisation (analyse + stratégie).
            try
            {
                var metaJson = state.Metadata != null ? JsonConvert.SerializeObject(state.Metadata) : string.Empty;
                await _db.UpdateSessionGenerationAsync(state.SessionId, metaJson, state.TestStrategy ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrateur] Persistance des artefacts de génération impossible.");
            }

            // Si le code généré ne compile toujours pas, on échoue clairement
            // (la session sera marquée "Erreur") plutôt que de poursuivre vers une découverte vide/incohérente.
            if (state.ValidationErrors.Count > 0)
            {
                throw new Exception(
                    $"La génération des tests a échoué : le code produit ne compile pas après {maxGenAttempts} tentative(s). " +
                    string.Join(" | ", state.ValidationErrors.Take(3)));
            }
        }

        // Boucle de génération bornée : Creator → Exporter → Verifier, avec ré-injection des
        // erreurs de build dans le Creator entre deux tentatives. S'arrête dès que le build est vert.
        private async Task RunCreatorVerifyLoopAsync(AgentState state, int maxAttempts)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await RunGenerationAgentAsync(_creator, state);
                await RunGenerationAgentAsync(_exporter, state);
                await RunGenerationAgentAsync(_verifier, state);

                if (state.ValidationErrors.Count == 0)
                {
                    _logger.LogInformation("[Orchestrateur] Tests générés compilés (tentative {N}).", attempt);
                    return;
                }
                _logger.LogWarning("[Orchestrateur] Build des tests générés en échec (tentative {N}/{Max}).", attempt, maxAttempts);
            }
        }

        // Adapte le PROJET SOURCE pour débloquer la compilation des tests UNIQUEMENT lorsque
        // l'échec de build est imputable à l'accessibilité de la source (CS0122 « inaccessible
        // due to protection level »). L'adaptation est déterministe et sans changement de
        // comportement (ajout d'un InternalsVisibleTo). Déroulé : snapshot de la source →
        // adaptation → ré-analyse → régénération/recompilation → conservation si le build passe,
        // sinon retour arrière intégral. Ne peut donc JAMAIS aggraver l'état.
        private async Task TryAdaptSourceAndRegenerateAsync(AgentState state, int maxAttempts)
        {
            if (!BuildFailureClassifier.ImplicatesSourceAccessibility(state.ValidationErrors))
            {
                _logger.LogInformation(
                    "[Orchestrateur] Échec de build non lié à l'accessibilité de la source — pas d'adaptation de la source.");
                return;
            }

            _logger.LogWarning(
                "[Orchestrateur] Build des tests bloqué par l'accessibilité de la source — tentative d'adaptation de la source.");

            var snapshot = SnapshotSourceFiles(state.SourceProjectPath);
            try
            {
                // 1. Adapte la source (ajoute InternalsVisibleTo).
                await RunGenerationAgentAsync(_sourceAdapter, state);

                // 2. Ré-analyse (les métadonnées peuvent changer) puis régénère/recompile.
                await RunGenerationAgentAsync(_analyzer, state);
                await RunGenerationAgentAsync(_decider, state);
                await RunCreatorVerifyLoopAsync(state, maxAttempts);

                if (state.ValidationErrors.Count == 0)
                {
                    _logger.LogInformation("[Orchestrateur] Adaptation de la source réussie — les tests compilent.");
                    return;
                }

                _logger.LogWarning(
                    "[Orchestrateur] L'adaptation de la source n'a pas débloqué le build — restauration de la source d'origine.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Orchestrateur] Adaptation de la source impossible — restauration de la source d'origine.");
            }

            // Retour arrière : restaure la source telle qu'elle a été soumise (supprime les
            // fichiers ajoutés, restaure le contenu d'origine) puis revérifie pour laisser un
            // état cohérent — le throw final s'appuiera sur ces ValidationErrors.
            RestoreSourceFiles(state.SourceProjectPath, snapshot);
            await RunGenerationAgentAsync(_verifier, state);
        }

        // Capture le contenu de tous les fichiers .cs du projet source (hors bin/obj) afin de
        // permettre un retour arrière exact si l'adaptation n'aboutit pas.
        private static Dictionary<string, string> SnapshotSourceFiles(string sourceDir)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return snapshot;

            var sep = Path.DirectorySeparatorChar;
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}")))
            {
                snapshot[Path.GetFullPath(f)] = File.ReadAllText(f);
            }
            return snapshot;
        }

        // Restaure le projet source à l'état capturé : supprime les .cs ajoutés depuis le
        // snapshot (ex. le shim InternalsVisibleTo) et réécrit le contenu d'origine.
        private static void RestoreSourceFiles(string sourceDir, Dictionary<string, string> snapshot)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir)) return;

            var sep = Path.DirectorySeparatorChar;
            var current = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
                .Select(Path.GetFullPath)
                .ToList();

            foreach (var f in current.Where(f => !snapshot.ContainsKey(f)))
            {
                try { File.Delete(f); } catch { /* best-effort */ }
            }
            foreach (var kv in snapshot)
            {
                try { File.WriteAllText(kv.Key, kv.Value); } catch { /* best-effort */ }
            }
        }

        // Relecture des valeurs attendues (Reviewer) après un build vert. Best-effort :
        // on conserve la dernière version compilable et on n'accepte la version relue que
        // si elle compile toujours — la relecture ne peut donc qu'améliorer, jamais casser.
        private async Task RunAssertionReviewAsync(AgentState state)
        {
            string compilingCode = state.GeneratedTestCode;
            try
            {
                await RunGenerationAgentAsync(_reviewer, state);

                // Aucune correction d'assertion → inutile de réexporter / revérifier.
                if (string.Equals(state.GeneratedTestCode.Trim(), compilingCode.Trim(), StringComparison.Ordinal))
                {
                    _logger.LogInformation("[Orchestrateur] Relecture : aucune correction d'assertion nécessaire.");
                    return;
                }

                await RunGenerationAgentAsync(_exporter, state);
                await RunGenerationAgentAsync(_verifier, state);

                if (state.ValidationErrors.Count == 0)
                {
                    _logger.LogInformation("[Orchestrateur] Relecture des valeurs attendues appliquée (build OK).");
                    return;
                }

                _logger.LogWarning("[Orchestrateur] La relecture casse le build — retour à la version générée précédente.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrateur] Relecture des assertions impossible — version générée conservée.");
            }

            // Retour arrière : on restaure la version qui compilait, on la réécrit sur le
            // disque et on revérifie pour laisser l'état (fichier + ValidationErrors) cohérent.
            state.GeneratedTestCode = compilingCode;
            await RunGenerationAgentAsync(_exporter, state);
            await RunGenerationAgentAsync(_verifier, state);
        }

        // Les agents de génération héritent de BaseAgent et journalisent eux-mêmes (Début/Succès/Erreur).
        private async Task RunGenerationAgentAsync(IAgent agent, AgentState state)
        {
            _logger.LogInformation("[Orchestrateur] → {Name}", agent.Name);
            try
            {
                await agent.ExecuteAsync(state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrateur] Échec de l'agent de génération {Name}", agent.Name);
                throw;
            }
        }

        private async Task RunFixAndVerifyLoopAsync(AgentState state)
        {
            int maxRetries = state.Settings.MaxRetries;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var toFix = state.TestCases.Where(t =>
                    (t.Action == TestAction.FixTest || t.Action == TestAction.FixCode) &&
                    t.Status == TestStatus.Red).ToList();

                if (toFix.Count == 0) break;

                _logger.LogInformation("[Orchestrateur] Tentative {N}/{Max}", attempt + 1, maxRetries);

                await RunAgentAsync(_fixer, state);
                await RunAgentAsync(_runner, state);

                int stillRed = state.TestCases.Count(t =>
                    t.Status == TestStatus.Red && t.Action != TestAction.Ignore);
                if (stillRed == 0) break;
            }

            bool allDone = state.TestCases.All(t =>
                t.Status == TestStatus.Green ||
                t.Status == TestStatus.Ignored ||
                t.Action == TestAction.Ignore);

            state.IsFinished = true;
            state.PipelineStatus = allDone ? "Terminé" : "EchecPartiel";

            await SaveSessionState(state, state.PipelineStatus);
            _logger.LogInformation("[Orchestrateur] Pipeline terminé : {Status}", state.PipelineStatus);
        }

        private async Task RunAgentAsync(IAgent agent, AgentState state)
        {
            _logger.LogInformation("[Orchestrateur] → {Name}", agent.Name);
            await _db.LogCommunicationAsync(state.SessionId, agent.Name, "Début");
            try
            {
                await agent.ExecuteAsync(state);
                await _db.LogCommunicationAsync(state.SessionId, agent.Name, "Succès");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrateur] Erreur dans {Name}", agent.Name);
                await _db.LogCommunicationAsync(state.SessionId, agent.Name, $"Erreur : {ex.Message}");
                throw;
            }
        }

        // Construit l'instantané GlobalState (compteurs de tests + progression multi-fichiers).
        private object BuildStatePayload(AgentState state) => new
        {
            Total = state.TestCases.Count,
            Green = state.TestCases.Count(t => t.Status == TestStatus.Green),
            Red = state.TestCases.Count(t => t.Status == TestStatus.Red),
            Ignored = state.TestCases.Count(t => t.Status == TestStatus.Ignored),
            AwaitingDecision = state.TestCases.Count(t => t.Status == TestStatus.EnAttenteDecision),
            Error = state.ErrorMessage ?? string.Empty,
            FilesTotal = state.FilesTotal,
            FilesDone = state.FilesDone,
            CurrentFile = state.CurrentFileName ?? string.Empty
        };

        // Met à jour uniquement l'instantané GlobalState (progression) en conservant le statut
        // « En_Cours » — utilisé pendant la boucle multi-fichiers pour le suivi en direct.
        private async Task UpdateProgressAsync(AgentState state)
        {
            var json = JsonConvert.SerializeObject(BuildStatePayload(state));
            await _db.UpdateSessionStateAsync(state.SessionId, json, "En_Cours");
        }

        private async Task SaveSessionState(AgentState state, string? status = null)
        {
            var json = JsonConvert.SerializeObject(BuildStatePayload(state));
            await _db.UpdateSessionStateAsync(state.SessionId, json, status ?? state.PipelineStatus);
            await PersistCodeSnapshotAsync(state);
        }

        // Persiste un instantané du code (source + tests) tant que le dossier de travail
        // existe encore, afin que l'onglet « Code » reste consultable même s'il venait à
        // disparaître (filet de sécurité). Best-effort : n'interrompt jamais le pipeline.
        private async Task PersistCodeSnapshotAsync(AgentState state)
        {
            try
            {
                var (codeJson, hasAny) = CodeSnapshotHelper.CaptureJson(state.SourceProjectPath, state.TargetProjectPath);
                if (hasAny)
                    await _db.UpdateSessionCodeAsync(state.SessionId, codeJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrateur] Instantané du code non persisté.");
            }
        }
    }
}
