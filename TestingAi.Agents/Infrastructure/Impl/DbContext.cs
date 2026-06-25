using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using TestingAi.Agents.Domain.Impl.Models;
using TestingAi.Agents.Domain.Intf.Services;

namespace TestingAi.Agents.Infrastructure.Impl
{
    public class DbContext : IDbContext
    {
        private readonly string _connectionString;

        public DbContext(string dbPath = "Data/TestingAi.Agents.db")
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _connectionString = $"Data Source={dbPath}";
        }

        private IDbConnection GetConnection() => new SqliteConnection(_connectionString);

        public async Task InitializeAsync()
        {
            using var db = GetConnection();
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS TestingSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TargetProject TEXT NOT NULL,
                    SourceProject TEXT NOT NULL DEFAULT '',
                    GlobalState TEXT NOT NULL DEFAULT '{}',
                    Status TEXT NOT NULL DEFAULT 'En_Cours',
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE TABLE IF NOT EXISTS TestCases (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId INTEGER NOT NULL,
                    TestName TEXT NOT NULL,
                    ClassName TEXT NOT NULL DEFAULT '',
                    MethodName TEXT NOT NULL DEFAULT '',
                    TestFilePath TEXT NOT NULL DEFAULT '',
                    SourceFilePath TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT 'Pending',
                    Action TEXT NOT NULL DEFAULT 'None',
                    ErrorMessage TEXT,
                    FixApplied TEXT,
                    RetryCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    UpdatedAt TEXT,
                    FOREIGN KEY (SessionId) REFERENCES TestingSessions(Id)
                );
                CREATE TABLE IF NOT EXISTS AgentA2ACommunication (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId INTEGER NOT NULL,
                    StepName TEXT NOT NULL,
                    ActionSummary TEXT NOT NULL,
                    Timestamp TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE TABLE IF NOT EXISTS AgentPrivateMemory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SessionId INTEGER NOT NULL,
                    AgentName TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    Timestamp TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE TABLE IF NOT EXISTS PipelineSettings (
                    Id INTEGER PRIMARY KEY DEFAULT 1,
                    HumanInterventionEnabled INTEGER NOT NULL DEFAULT 0,
                    MaxRetries INTEGER NOT NULL DEFAULT 3,
                    PreferredProvider TEXT NOT NULL DEFAULT 'Gemini'
                );
                INSERT OR IGNORE INTO PipelineSettings (Id, HumanInterventionEnabled, MaxRetries, PreferredProvider)
                VALUES (1, 0, 3, 'Gemini');
            ");

            // ── Migrations légères (colonnes ajoutées après coup) ────────────────
            await EnsureColumnAsync(db, "TestingSessions", "Metadata", "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(db, "TestingSessions", "TestStrategy", "TEXT NOT NULL DEFAULT ''");
            // Commande lancée + résultat (agents déterministes / TestRunner) pour la timeline A2A.
            await EnsureColumnAsync(db, "AgentA2ACommunication", "CommandText", "TEXT");
            await EnsureColumnAsync(db, "AgentA2ACommunication", "CommandOutput", "TEXT");
        }

        // Ajoute une colonne si elle n'existe pas (SQLite n'a pas ADD COLUMN IF NOT EXISTS).
        private static async Task EnsureColumnAsync(IDbConnection db, string table, string column, string definition)
        {
            var cols = await db.QueryAsync<dynamic>($"PRAGMA table_info({table})");
            bool exists = cols.Any(c => string.Equals((string)c.name, column, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                await db.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }

        // ── Sessions ─────────────────────────────────────────────────────────

        public async Task<int> CreateSessionAsync(string targetProject, string sourceProject)
        {
            using var db = GetConnection();
            return await db.ExecuteScalarAsync<int>(
                "INSERT INTO TestingSessions (TargetProject, SourceProject) VALUES (@targetProject, @sourceProject); SELECT last_insert_rowid();",
                new { targetProject, sourceProject });
        }

        public async Task<TestingSession?> GetSessionAsync(int sessionId)
        {
            using var db = GetConnection();
            return await db.QueryFirstOrDefaultAsync<TestingSession>(
                "SELECT * FROM TestingSessions WHERE Id = @sessionId", new { sessionId });
        }

        public async Task<IEnumerable<TestingSession>> GetAllSessionsAsync()
        {
            using var db = GetConnection();
            return await db.QueryAsync<TestingSession>(
                "SELECT * FROM TestingSessions ORDER BY Id DESC");
        }

        public async Task UpdateSessionStateAsync(int sessionId, string globalState, string status)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "UPDATE TestingSessions SET GlobalState = @globalState, Status = @status WHERE Id = @sessionId",
                new { sessionId, globalState, status });
        }

        public async Task UpdateSessionGenerationAsync(int sessionId, string metadata, string testStrategy)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "UPDATE TestingSessions SET Metadata = @metadata, TestStrategy = @testStrategy WHERE Id = @sessionId",
                new { sessionId, metadata, testStrategy });
        }

        // Réinitialise une session pour une relance complète : supprime les tests,
        // la timeline A2A et la mémoire des agents, et remet la session à l'état initial.
        public async Task ResetSessionForRerunAsync(int sessionId)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(@"
                DELETE FROM TestCases WHERE SessionId = @sessionId;
                DELETE FROM AgentA2ACommunication WHERE SessionId = @sessionId;
                DELETE FROM AgentPrivateMemory WHERE SessionId = @sessionId;
                UPDATE TestingSessions
                   SET GlobalState = '{}', Status = 'En_Cours', Metadata = '', TestStrategy = ''
                 WHERE Id = @sessionId;",
                new { sessionId });
        }

        // ── TestCases ─────────────────────────────────────────────────────────

        public async Task<int> UpsertTestCaseAsync(TestCase tc)
        {
            using var db = GetConnection();
            var existing = await db.QueryFirstOrDefaultAsync<int?>(
                "SELECT Id FROM TestCases WHERE SessionId = @SessionId AND TestName = @TestName",
                new { tc.SessionId, tc.TestName });

            if (existing.HasValue)
            {
                await db.ExecuteAsync(
                    "UPDATE TestCases SET Status = @Status, Action = @Action, UpdatedAt = datetime('now') WHERE Id = @Id",
                    new { Status = tc.Status.ToString(), Action = tc.Action.ToString(), Id = existing.Value });
                return existing.Value;
            }

            return await db.ExecuteScalarAsync<int>(
                @"INSERT INTO TestCases (SessionId, TestName, ClassName, MethodName, TestFilePath, SourceFilePath, Status, Action, CreatedAt)
                  VALUES (@SessionId, @TestName, @ClassName, @MethodName, @TestFilePath, @SourceFilePath, @Status, @Action, datetime('now'));
                  SELECT last_insert_rowid();",
                new
                {
                    tc.SessionId, tc.TestName, tc.ClassName, tc.MethodName,
                    tc.TestFilePath, tc.SourceFilePath,
                    Status = tc.Status.ToString(),
                    Action = tc.Action.ToString()
                });
        }

        public async Task<TestCase?> GetTestCaseAsync(int id)
        {
            using var db = GetConnection();
            var row = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM TestCases WHERE Id = @id", new { id });
            return row == null ? null : MapTestCase(row);
        }

        public async Task<IEnumerable<TestCase>> GetTestCasesAsync(int sessionId)
        {
            using var db = GetConnection();
            var rows = await db.QueryAsync<dynamic>(
                "SELECT * FROM TestCases WHERE SessionId = @sessionId ORDER BY Id", new { sessionId });
            var result = new List<TestCase>();
            foreach (var r in rows) result.Add(MapTestCase(r));
            return result;
        }

        public async Task UpdateTestCaseStatusAsync(int id, TestStatus status, string? errorMessage = null)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "UPDATE TestCases SET Status = @status, ErrorMessage = @errorMessage, UpdatedAt = datetime('now') WHERE Id = @id",
                new { id, status = status.ToString(), errorMessage });
        }

        public async Task UpdateTestCaseActionAsync(int id, TestAction action)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "UPDATE TestCases SET Action = @action, UpdatedAt = datetime('now') WHERE Id = @id",
                new { id, action = action.ToString() });
        }

        public async Task UpdateTestCaseFixAsync(int id, string fixApplied, int retryCount)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "UPDATE TestCases SET FixApplied = @fixApplied, RetryCount = @retryCount, UpdatedAt = datetime('now') WHERE Id = @id",
                new { id, fixApplied, retryCount });
        }

        // ── Settings ──────────────────────────────────────────────────────────

        public async Task<PipelineSettings> GetSettingsAsync()
        {
            using var db = GetConnection();
            var row = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM PipelineSettings WHERE Id = 1");
            if (row == null) return new PipelineSettings();
            return new PipelineSettings
            {
                HumanInterventionEnabled = Convert.ToInt32(row.HumanInterventionEnabled ?? 0) == 1,
                MaxRetries = Convert.ToInt32(row.MaxRetries ?? 3),
                PreferredProvider = (string)(row.PreferredProvider ?? "Gemini")
            };
        }

        public async Task UpdateSettingsAsync(PipelineSettings settings)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "UPDATE PipelineSettings SET HumanInterventionEnabled = @h, MaxRetries = @r, PreferredProvider = @p WHERE Id = 1",
                new { h = settings.HumanInterventionEnabled ? 1 : 0, r = settings.MaxRetries, p = settings.PreferredProvider });
        }

        // ── Logs ──────────────────────────────────────────────────────────────

        public Task LogCommunicationAsync(int sessionId, string stepName, string actionSummary)
            => LogCommunicationAsync(sessionId, stepName, actionSummary, null, null);

        public async Task LogCommunicationAsync(int sessionId, string stepName, string actionSummary, string? commandText, string? commandOutput)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "INSERT INTO AgentA2ACommunication (SessionId, StepName, ActionSummary, CommandText, CommandOutput) VALUES (@sessionId, @stepName, @actionSummary, @commandText, @commandOutput)",
                new { sessionId, stepName, actionSummary, commandText, commandOutput });
        }

        public async Task SavePrivateMemoryAsync(int sessionId, string agentName, string role, string content)
        {
            using var db = GetConnection();
            await db.ExecuteAsync(
                "INSERT INTO AgentPrivateMemory (SessionId, AgentName, Role, Content) VALUES (@sessionId, @agentName, @role, @content)",
                new { sessionId, agentName, role, content });
        }

        public async Task<IEnumerable<AgentA2ACommunication>> GetCommunicationsAsync(int sessionId)
        {
            using var db = GetConnection();
            return await db.QueryAsync<AgentA2ACommunication>(
                "SELECT * FROM AgentA2ACommunication WHERE SessionId = @sessionId ORDER BY Timestamp",
                new { sessionId });
        }

        public async Task<IEnumerable<AgentPrivateMemory>> GetPrivateMemoryAsync(int sessionId)
        {
            using var db = GetConnection();
            return await db.QueryAsync<AgentPrivateMemory>(
                "SELECT * FROM AgentPrivateMemory WHERE SessionId = @sessionId ORDER BY Id",
                new { sessionId });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static TestCase MapTestCase(dynamic r) => new TestCase
        {
            Id = (int)r.Id,
            SessionId = (int)r.SessionId,
            TestName = r.TestName ?? "",
            ClassName = r.ClassName ?? "",
            MethodName = r.MethodName ?? "",
            TestFilePath = r.TestFilePath ?? "",
            SourceFilePath = r.SourceFilePath ?? "",
            Status = Enum.TryParse<TestStatus>((string?)(r.Status?.ToString()), out TestStatus s) ? s : TestStatus.Pending,
            Action = Enum.TryParse<TestAction>((string?)(r.Action?.ToString()), out TestAction a) ? a : TestAction.None,
            ErrorMessage = r.ErrorMessage,
            FixApplied = r.FixApplied,
            RetryCount = (int)(r.RetryCount ?? 0),
            CreatedAt = r.CreatedAt ?? "",
            UpdatedAt = r.UpdatedAt
        };
    }
}
