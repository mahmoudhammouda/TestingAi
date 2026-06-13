using System;
using System.Collections.Generic;
using System.Data;
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
            _connectionString = $"Data Source={dbPath}";
        }

        private IDbConnection GetConnection() => new SqliteConnection(_connectionString);

        public async Task<int> CreateSessionAsync(string targetProject)
        {
            using var db = GetConnection();
            const string sql = "INSERT INTO TestingSessions (TargetProject, Status) VALUES (@targetProject, 'En_Cours'); SELECT last_insert_rowid();";
            return await db.ExecuteScalarAsync<int>(sql, new { targetProject });
        }

        public async Task UpdateSessionStateAsync(int sessionId, string state, string status)
        {
            using var db = GetConnection();
            const string sql = "UPDATE TestingSessions SET GlobalState = @state, Status = @status WHERE Id = @sessionId";
            await db.ExecuteAsync(sql, new { sessionId, state, status });
        }

        public async Task<TestingSession> GetSessionAsync(int sessionId)
        {
            using var db = GetConnection();
            return await db.QueryFirstOrDefaultAsync<TestingSession>("SELECT * FROM TestingSessions WHERE Id = @sessionId", new { sessionId });
        }

        public async Task LogCommunicationAsync(int sessionId, string stepName, string actionSummary)
        {
            using var db = GetConnection();
            const string sql = "INSERT INTO AgentA2ACommunication (SessionId, StepName, ActionSummary) VALUES (@sessionId, @stepName, @actionSummary)";
            await db.ExecuteAsync(sql, new { sessionId, stepName, actionSummary });
        }

        public async Task<IEnumerable<AgentA2ACommunication>> GetCommunicationsAsync(int sessionId)
        {
            using var db = GetConnection();
            return await db.QueryAsync<AgentA2ACommunication>("SELECT * FROM AgentA2ACommunication WHERE SessionId = @sessionId ORDER BY Timestamp", new { sessionId });
        }

        public async Task SavePrivateMemoryAsync(int sessionId, string agentName, string role, string content)
        {
            using var db = GetConnection();
            const string sql = "INSERT INTO AgentPrivateMemory (SessionId, AgentName, Role, Content) VALUES (@sessionId, @agentName, @role, @content)";
            await db.ExecuteAsync(sql, new { sessionId, agentName, role, content });
        }

        public async Task<IEnumerable<AgentPrivateMemory>> GetPrivateMemoryAsync(int sessionId, string agentName)
        {
            using var db = GetConnection();
            return await db.QueryAsync<AgentPrivateMemory>("SELECT * FROM AgentPrivateMemory WHERE SessionId = @sessionId AND AgentName = @agentName ORDER BY Timestamp", new { sessionId, agentName });
        }
    }
}
