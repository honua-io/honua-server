// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// PostgreSQL implementation of <see cref="IMigrationRunCatalog"/>. Persists
/// run lifecycle records into <c>honua.migration_runs</c> using ON CONFLICT
/// guards so apply services can retry safely. Slice 5 of issue #1015.
/// </summary>
internal sealed partial class PostgresMigrationRunCatalog : IMigrationRunCatalog
{
    private const int MaxPageLimit = 100;
    private const int DefaultPageLimit = 25;

    private readonly string _connectionString;
    private readonly ILogger<PostgresMigrationRunCatalog> _logger;

    public PostgresMigrationRunCatalog(
        string connectionString,
        ILogger<PostgresMigrationRunCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MigrationRunRecord> RecordStartedAsync(
        MigrationRunRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SourceKind);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // ON CONFLICT DO NOTHING keeps re-recording the same run id a no-op
        // so apply services can retry safely. The follow-up SELECT returns
        // the persisted row regardless of insert/conflict.
        const string insertSql = """
            INSERT INTO honua.migration_runs (
                run_id,
                source_kind,
                source_url,
                source_display_name,
                status,
                started_at,
                completed_at,
                evidence_pack_ref,
                evidence_pack_fingerprint,
                evidence_pack_body,
                status_note
            )
            VALUES (
                @runId,
                @sourceKind,
                @sourceUrl,
                @sourceDisplayName,
                @status,
                @startedAt,
                @completedAt,
                @evidenceRef,
                @evidenceFingerprint,
                CAST(@evidenceBody AS jsonb),
                @statusNote
            )
            ON CONFLICT (run_id) DO NOTHING;
            """;

        await using (var insert = new NpgsqlCommand(insertSql, connection))
        {
            insert.Parameters.AddWithValue("@runId", record.RunId);
            insert.Parameters.AddWithValue("@sourceKind", record.SourceKind);
            insert.Parameters.AddWithValue("@sourceUrl", record.SourceUrl ?? string.Empty);
            insert.Parameters.AddWithValue("@sourceDisplayName", (object?)record.SourceDisplayName ?? DBNull.Value);
            insert.Parameters.AddWithValue("@status", StatusToText(record.Status));
            insert.Parameters.AddWithValue("@startedAt", record.StartedAt.UtcDateTime);
            insert.Parameters.AddWithValue("@completedAt", (object?)record.CompletedAt?.UtcDateTime ?? DBNull.Value);
            insert.Parameters.AddWithValue("@evidenceRef", (object?)record.EvidencePackRef ?? DBNull.Value);
            insert.Parameters.AddWithValue("@evidenceFingerprint", (object?)record.EvidencePackFingerprint ?? DBNull.Value);
            insert.Parameters.AddWithValue("@evidenceBody", DBNull.Value);
            insert.Parameters.AddWithValue("@statusNote", (object?)record.StatusNote ?? DBNull.Value);

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var persisted = await GetInternalAsync(connection, record.RunId, cancellationToken).ConfigureAwait(false)
            ?? record;
        Log.RunStarted(_logger, persisted.RunId, persisted.SourceKind, StatusToText(persisted.Status));
        return persisted;
    }

    public async Task<MigrationRunRecord?> RecordCompletedAsync(
        string runId,
        MigrationRunStatus status,
        DateTimeOffset completedAt,
        string? evidencePackRef,
        string? evidencePackFingerprint,
        string? evidencePackBody,
        string? statusNote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (status == MigrationRunStatus.Running)
        {
            throw new ArgumentException("RecordCompletedAsync requires a terminal status.", nameof(status));
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Terminal state is sticky: the WHERE clause refuses to overwrite an
        // already-terminal row so a late success notification cannot silently
        // erase a cancel or failure.
        const string updateSql = """
            UPDATE honua.migration_runs
            SET status = @status,
                completed_at = @completedAt,
                evidence_pack_ref = COALESCE(@evidenceRef, evidence_pack_ref),
                evidence_pack_fingerprint = COALESCE(@evidenceFingerprint, evidence_pack_fingerprint),
                evidence_pack_body = COALESCE(CAST(@evidenceBody AS jsonb), evidence_pack_body),
                status_note = COALESCE(@statusNote, status_note)
            WHERE run_id = @runId
              AND status = 'running';
            """;

        await using (var update = new NpgsqlCommand(updateSql, connection))
        {
            update.Parameters.AddWithValue("@runId", runId);
            update.Parameters.AddWithValue("@status", StatusToText(status));
            update.Parameters.AddWithValue("@completedAt", completedAt.UtcDateTime);
            update.Parameters.AddWithValue("@evidenceRef", (object?)evidencePackRef ?? DBNull.Value);
            update.Parameters.AddWithValue("@evidenceFingerprint", (object?)evidencePackFingerprint ?? DBNull.Value);
            var bodyParam = update.Parameters.Add("@evidenceBody", NpgsqlDbType.Text);
            bodyParam.Value = (object?)evidencePackBody ?? DBNull.Value;
            update.Parameters.AddWithValue("@statusNote", (object?)statusNote ?? DBNull.Value);

            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var persisted = await GetInternalAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            Log.RunCompleted(_logger, persisted.RunId, StatusToText(persisted.Status));
        }
        return persisted;
    }

    public async Task<MigrationRunRecord?> CancelAsync(
        string runId,
        DateTimeOffset cancelledAt,
        string? statusNote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            UPDATE honua.migration_runs
            SET status = 'cancelled',
                completed_at = @cancelledAt,
                status_note = COALESCE(@statusNote, status_note)
            WHERE run_id = @runId
              AND status = 'running';
            """;

        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@runId", runId);
            command.Parameters.AddWithValue("@cancelledAt", cancelledAt.UtcDateTime);
            command.Parameters.AddWithValue("@statusNote", (object?)statusNote ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var persisted = await GetInternalAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            Log.RunCancelled(_logger, persisted.RunId, StatusToText(persisted.Status));
        }
        return persisted;
    }

    public async Task<MigrationRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(connection, runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationRunListPage> ListAsync(
        MigrationRunListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = query.Limit <= 0 ? DefaultPageLimit : Math.Min(query.Limit, MaxPageLimit);
        var offset = Math.Max(0, query.Offset);
        var sourceKindFilter = string.IsNullOrWhiteSpace(query.SourceKind) ? null : query.SourceKind!.Trim().ToLowerInvariant();
        var statusFilter = query.Status.HasValue ? StatusToText(query.Status.Value) : null;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string countSql = """
            SELECT COUNT(*) FROM honua.migration_runs
            WHERE (@sourceKind::text IS NULL OR LOWER(source_kind) = @sourceKind::text)
              AND (@status::text IS NULL OR status = @status::text);
            """;

        long total;
        await using (var countCmd = new NpgsqlCommand(countSql, connection))
        {
            countCmd.Parameters.Add(new NpgsqlParameter("@sourceKind", NpgsqlDbType.Text) { Value = (object?)sourceKindFilter ?? DBNull.Value });
            countCmd.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Text) { Value = (object?)statusFilter ?? DBNull.Value });
            var raw = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            total = raw switch
            {
                long l => l,
                int i => i,
                _ => Convert.ToInt64(raw, CultureInfo.InvariantCulture)
            };
        }

        const string listSql = """
            SELECT run_id,
                   source_kind,
                   source_url,
                   source_display_name,
                   status,
                   started_at,
                   completed_at,
                   evidence_pack_ref,
                   evidence_pack_fingerprint,
                   status_note
            FROM honua.migration_runs
            WHERE (@sourceKind::text IS NULL OR LOWER(source_kind) = @sourceKind::text)
              AND (@status::text IS NULL OR status = @status::text)
            ORDER BY started_at DESC
            LIMIT @limit OFFSET @offset;
            """;

        var items = new List<MigrationRunRecord>(limit);
        await using (var listCmd = new NpgsqlCommand(listSql, connection))
        {
            listCmd.Parameters.Add(new NpgsqlParameter("@sourceKind", NpgsqlDbType.Text) { Value = (object?)sourceKindFilter ?? DBNull.Value });
            listCmd.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Text) { Value = (object?)statusFilter ?? DBNull.Value });
            listCmd.Parameters.AddWithValue("@limit", limit);
            listCmd.Parameters.AddWithValue("@offset", offset);

            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadRow(reader));
            }
        }

        return new MigrationRunListPage
        {
            Items = items,
            TotalCount = total,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<string?> GetEvidencePackAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT evidence_pack_body::text
            FROM honua.migration_runs
            WHERE run_id = @runId
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@runId", runId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            null => null,
            DBNull => null,
            string body => body,
            _ => result.ToString()
        };
    }

    private static async Task<MigrationRunRecord?> GetInternalAsync(
        NpgsqlConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT run_id,
                   source_kind,
                   source_url,
                   source_display_name,
                   status,
                   started_at,
                   completed_at,
                   evidence_pack_ref,
                   evidence_pack_fingerprint,
                   status_note
            FROM honua.migration_runs
            WHERE run_id = @runId
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRow(reader);
    }

    private static MigrationRunRecord ReadRow(NpgsqlDataReader reader)
    {
        var startedAt = reader.GetDateTime(reader.GetOrdinal("started_at"));
        var completedOrdinal = reader.GetOrdinal("completed_at");
        DateTimeOffset? completedAt = reader.IsDBNull(completedOrdinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(completedOrdinal), DateTimeKind.Utc));

        return new MigrationRunRecord
        {
            RunId = reader.GetString(reader.GetOrdinal("run_id")),
            SourceKind = reader.GetString(reader.GetOrdinal("source_kind")),
            SourceUrl = reader.GetString(reader.GetOrdinal("source_url")),
            SourceDisplayName = reader.IsDBNull(reader.GetOrdinal("source_display_name"))
                ? null
                : reader.GetString(reader.GetOrdinal("source_display_name")),
            Status = StatusFromText(reader.GetString(reader.GetOrdinal("status"))),
            StartedAt = new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)),
            CompletedAt = completedAt,
            EvidencePackRef = reader.IsDBNull(reader.GetOrdinal("evidence_pack_ref"))
                ? null
                : reader.GetString(reader.GetOrdinal("evidence_pack_ref")),
            EvidencePackFingerprint = reader.IsDBNull(reader.GetOrdinal("evidence_pack_fingerprint"))
                ? null
                : reader.GetString(reader.GetOrdinal("evidence_pack_fingerprint")),
            StatusNote = reader.IsDBNull(reader.GetOrdinal("status_note"))
                ? null
                : reader.GetString(reader.GetOrdinal("status_note"))
        };
    }

    internal static string StatusToText(MigrationRunStatus status) => status switch
    {
        MigrationRunStatus.Running => "running",
        MigrationRunStatus.Succeeded => "succeeded",
        MigrationRunStatus.Failed => "failed",
        MigrationRunStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown migration run status.")
    };

    internal static MigrationRunStatus StatusFromText(string text) => text switch
    {
        "running" => MigrationRunStatus.Running,
        "succeeded" => MigrationRunStatus.Succeeded,
        "failed" => MigrationRunStatus.Failed,
        "cancelled" => MigrationRunStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown migration run status '{text}'.")
    };

    private static partial class Log
    {
        [LoggerMessage(7970, LogLevel.Information, "Migration run catalog recorded start for run '{RunId}' (source={SourceKind}, status={Status})")]
        public static partial void RunStarted(ILogger logger, string runId, string sourceKind, string status);

        [LoggerMessage(7971, LogLevel.Information, "Migration run catalog recorded completion for run '{RunId}' (status={Status})")]
        public static partial void RunCompleted(ILogger logger, string runId, string status);

        [LoggerMessage(7972, LogLevel.Information, "Migration run catalog cancelled run '{RunId}' (status={Status})")]
        public static partial void RunCancelled(ILogger logger, string runId, string status);
    }
}
