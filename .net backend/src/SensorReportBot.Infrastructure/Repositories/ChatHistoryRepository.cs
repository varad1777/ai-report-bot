namespace SensorReportBot.Infrastructure.Repositories;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SensorReportBot.Application.Interfaces;
using SensorReportBot.Domain.Entities;

public class ChatHistoryRepository : IChatHistoryRepository
{
    private readonly string _connectionString;
    private readonly ILogger<ChatHistoryRepository> _logger;
    private bool _tableInitialized = false;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public ChatHistoryRepository(IConfiguration configuration, ILogger<ChatHistoryRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("TimescaleDb")
            ?? "Host=localhost;Port=5432;Database=iam_db;Username=iam_user;Password=iam_pass";
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    private async Task EnsureTableExistsAsync(CancellationToken ct)
    {
        if (_tableInitialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_tableInitialized) return;

            using var conn = CreateConnection();
            const string sql = @"
                CREATE TABLE IF NOT EXISTS chat_messages (
                    id BIGSERIAL PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    metadata TEXT,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );
                ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS metadata TEXT;
                CREATE INDEX IF NOT EXISTS idx_chat_messages_session ON chat_messages (session_id, created_at ASC);";

            await conn.ExecuteAsync(sql);
            _tableInitialized = true;
            _logger.LogInformation("PostgreSQL chat_messages table verified/initialized.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize chat_messages table in PostgreSQL.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task SaveMessageAsync(string sessionId, string role, string content, string? metadata = null, CancellationToken ct = default)
    {
        await EnsureTableExistsAsync(ct);
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO chat_messages (session_id, role, content, metadata, created_at)
            VALUES (@SessionId, @Role, @Content, @Metadata, @CreatedAt);";

        await conn.ExecuteAsync(sql, new
        {
            SessionId = sessionId,
            Role = role,
            Content = content,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow
        });
    }

    public async Task<IReadOnlyList<ChatMessageEntity>> GetRecentHistoryAsync(string sessionId, int limit = 50, CancellationToken ct = default)
    {
        await EnsureTableExistsAsync(ct);
        using var conn = CreateConnection();
        const string sql = @"
            SELECT id AS Id, session_id AS SessionId, role AS Role, content AS Content, metadata AS Metadata, created_at AS CreatedAt
            FROM (
                SELECT id, session_id, role, content, metadata, created_at
                FROM chat_messages
                WHERE session_id = @SessionId
                ORDER BY created_at DESC
                LIMIT @Limit
            ) sub
            ORDER BY created_at ASC;";

        var results = await conn.QueryAsync<ChatMessageEntity>(sql, new { SessionId = sessionId, Limit = limit });
        return results.ToList();
    }

    public async Task<string?> GetLatestAssistantMetadataAsync(string sessionId, CancellationToken ct = default)
    {
        await EnsureTableExistsAsync(ct);
        using var conn = CreateConnection();
        const string sql = @"
            SELECT metadata
            FROM chat_messages
            WHERE session_id = @SessionId AND role = 'assistant' AND metadata IS NOT NULL
            ORDER BY created_at DESC, id DESC
            LIMIT 1;";
        return await conn.QueryFirstOrDefaultAsync<string?>(sql, new { SessionId = sessionId });
    }

    public async Task ClearHistoryAsync(string sessionId, CancellationToken ct = default)
    {
        await EnsureTableExistsAsync(ct);
        using var conn = CreateConnection();
        const string sql = "DELETE FROM chat_messages WHERE session_id = @SessionId;";
        await conn.ExecuteAsync(sql, new { SessionId = sessionId });
    }
}
