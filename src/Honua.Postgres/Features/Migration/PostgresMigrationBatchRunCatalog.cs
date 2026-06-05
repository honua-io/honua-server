// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// PostgreSQL implementation of <see cref="IMigrationBatchRunCatalog"/> (issue
/// #1253). Persists batch composition, ordering, and per-child status into
/// <c>honua.migration_batch_runs</c> and <c>honua.migration_batch_children</c>.
/// Uses ON CONFLICT guards and sticky-terminal WHERE clauses so the orchestrator
/// can advance/retry safely.
/// </summary>
internal sealed partial class PostgresMigrationBatchRunCatalog : IMigrationBatchRunCatalog
{
    private static readonly JsonSerializerOptions DependsOnOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly ILogger<PostgresMigrationBatchRunCatalog> _logger;

    public PostgresMigrationBatchRunCatalog(
        string connectionString,
        ILogger<PostgresMigrationBatchRunCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MigrationBatchRunRecord> CreateAsync(
        MigrationBatchRunRecord record,
        string? manifestBody,
        IReadOnlyList<MigrationBatchChildRecord> children,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.BatchId);
        ArgumentNullException.ThrowIfNull(children);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string insertBatchSql = """
            INSERT INTO honua.migration_batch_runs (
                batch_id, source_kind, source_url, source_display_name, status,
                started_at, completed_at, total_children, succeeded_children,
                failed_children, cancelled_children, apply_relationships,
                relationships_applied, manifest_body, status_note
            )
            VALUES (
                @batchId, @sourceKind, @sourceUrl, @sourceDisplayName, @status,
                @startedAt, @completedAt, @totalChildren, @succeeded,
                @failed, @cancelled, @applyRelationships,
                @relationshipsApplied, CAST(@manifestBody AS jsonb), @statusNote
            )
            ON CONFLICT (batch_id) DO NOTHING;
            """;

        var inserted = false;
        await using (var insert = new NpgsqlCommand(insertBatchSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("@batchId", record.BatchId);
            insert.Parameters.AddWithValue("@sourceKind", record.SourceKind);
            insert.Parameters.AddWithValue("@sourceUrl", record.SourceUrl ?? string.Empty);
            insert.Parameters.AddWithValue("@sourceDisplayName", (object?)record.SourceDisplayName ?? DBNull.Value);
            insert.Parameters.AddWithValue("@status", BatchStatusToText(record.Status));
            insert.Parameters.AddWithValue("@startedAt", record.StartedAt.UtcDateTime);
            insert.Parameters.AddWithValue("@completedAt", (object?)record.CompletedAt?.UtcDateTime ?? DBNull.Value);
            insert.Parameters.AddWithValue("@totalChildren", record.TotalChildren);
            insert.Parameters.AddWithValue("@succeeded", record.SucceededChildren);
            insert.Parameters.AddWithValue("@failed", record.FailedChildren);
            insert.Parameters.AddWithValue("@cancelled", record.CancelledChildren);
            insert.Parameters.AddWithValue("@applyRelationships", record.ApplyRelationships);
            insert.Parameters.AddWithValue("@relationshipsApplied", record.RelationshipsApplied);
            var bodyParam = insert.Parameters.Add("@manifestBody", NpgsqlDbType.Text);
            bodyParam.Value = (object?)manifestBody ?? DBNull.Value;
            insert.Parameters.AddWithValue("@statusNote", (object?)record.StatusNote ?? DBNull.Value);

            var affected = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            inserted = affected > 0;
        }

        if (inserted)
        {
            const string insertChildSql = """
                INSERT INTO honua.migration_batch_children (
                    batch_id, ordinal, source_resource_id, service_url, source_layer_id,
                    table_name, target_schema, service_name, depends_on, status,
                    job_id, published_layer_id, status_note, updated_at
                )
                VALUES (
                    @batchId, @ordinal, @sourceResourceId, @serviceUrl, @sourceLayerId,
                    @tableName, @targetSchema, @serviceName, CAST(@dependsOn AS jsonb), @status,
                    @jobId, @publishedLayerId, @statusNote, @updatedAt
                )
                ON CONFLICT (batch_id, ordinal) DO NOTHING;
                """;

            foreach (var child in children)
            {
                await using var insertChild = new NpgsqlCommand(insertChildSql, connection, transaction);
                insertChild.Parameters.AddWithValue("@batchId", record.BatchId);
                insertChild.Parameters.AddWithValue("@ordinal", child.Ordinal);
                insertChild.Parameters.AddWithValue("@sourceResourceId", child.SourceResourceId);
                insertChild.Parameters.AddWithValue("@serviceUrl", child.ServiceUrl);
                insertChild.Parameters.AddWithValue("@sourceLayerId", child.SourceLayerId);
                insertChild.Parameters.AddWithValue("@tableName", child.TableName);
                insertChild.Parameters.AddWithValue("@targetSchema", (object?)child.TargetSchema ?? DBNull.Value);
                insertChild.Parameters.AddWithValue("@serviceName", (object?)child.ServiceName ?? DBNull.Value);
                var dependsParam = insertChild.Parameters.Add("@dependsOn", NpgsqlDbType.Text);
                dependsParam.Value = JsonSerializer.Serialize(child.DependsOn, DependsOnOptions);
                insertChild.Parameters.AddWithValue("@status", ChildStatusToText(child.Status));
                insertChild.Parameters.AddWithValue("@jobId", (object?)child.JobId ?? DBNull.Value);
                insertChild.Parameters.AddWithValue("@publishedLayerId", (object?)child.PublishedLayerId ?? DBNull.Value);
                insertChild.Parameters.AddWithValue("@statusNote", (object?)child.StatusNote ?? DBNull.Value);
                insertChild.Parameters.AddWithValue("@updatedAt", child.UpdatedAt.UtcDateTime);

                await insertChild.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var persisted = await GetInternalAsync(connection, record.BatchId, cancellationToken).ConfigureAwait(false) ?? record;
        Log.BatchCreated(_logger, persisted.BatchId, persisted.SourceKind, persisted.TotalChildren);
        return persisted;
    }

    public async Task<MigrationBatchRunRecord?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetInternalAsync(connection, batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MigrationBatchChildRecord>> GetChildrenAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT batch_id, ordinal, source_resource_id, service_url, source_layer_id,
                   table_name, target_schema, service_name, depends_on::text, status,
                   job_id, published_layer_id, status_note, updated_at
            FROM honua.migration_batch_children
            WHERE batch_id = @batchId
            ORDER BY ordinal ASC;
            """;

        var children = new List<MigrationBatchChildRecord>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            children.Add(ReadChild(reader));
        }

        return children;
    }

    public async Task<string?> GetManifestBodyAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT manifest_body::text FROM honua.migration_batch_runs
            WHERE batch_id = @batchId LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@batchId", batchId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            null or DBNull => null,
            string body => body,
            _ => result.ToString()
        };
    }

    public async Task<MigrationBatchChildRecord?> UpdateChildAsync(
        string batchId,
        int ordinal,
        MigrationBatchChildStatus status,
        string? jobId,
        int? publishedLayerId,
        string? statusNote,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Terminal child states are sticky: don't overwrite a row that already
        // reached succeeded/failed/cancelled/needs-review.
        const string sql = """
            UPDATE honua.migration_batch_children
            SET status = @status,
                job_id = COALESCE(@jobId, job_id),
                published_layer_id = COALESCE(@publishedLayerId, published_layer_id),
                status_note = COALESCE(@statusNote, status_note),
                updated_at = @updatedAt
            WHERE batch_id = @batchId
              AND ordinal = @ordinal
              AND status IN ('pending','running');
            """;

        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@batchId", batchId);
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@status", ChildStatusToText(status));
            command.Parameters.AddWithValue("@jobId", (object?)jobId ?? DBNull.Value);
            command.Parameters.AddWithValue("@publishedLayerId", (object?)publishedLayerId ?? DBNull.Value);
            command.Parameters.AddWithValue("@statusNote", (object?)statusNote ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", updatedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetChildInternalAsync(connection, batchId, ordinal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationBatchRunRecord?> UpdateBatchAsync(
        string batchId,
        MigrationBatchRunStatus status,
        int succeededChildren,
        int failedChildren,
        int cancelledChildren,
        DateTimeOffset? completedAt,
        bool? relationshipsApplied,
        string? statusNote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Terminal batch states are sticky.
        const string sql = """
            UPDATE honua.migration_batch_runs
            SET status = @status,
                succeeded_children = @succeeded,
                failed_children = @failed,
                cancelled_children = @cancelled,
                completed_at = COALESCE(@completedAt, completed_at),
                relationships_applied = COALESCE(@relationshipsApplied, relationships_applied),
                status_note = COALESCE(@statusNote, status_note)
            WHERE batch_id = @batchId
              AND status = 'running';
            """;

        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@batchId", batchId);
            command.Parameters.AddWithValue("@status", BatchStatusToText(status));
            command.Parameters.AddWithValue("@succeeded", succeededChildren);
            command.Parameters.AddWithValue("@failed", failedChildren);
            command.Parameters.AddWithValue("@cancelled", cancelledChildren);
            command.Parameters.AddWithValue("@completedAt", (object?)completedAt?.UtcDateTime ?? DBNull.Value);
            command.Parameters.AddWithValue("@relationshipsApplied", (object?)relationshipsApplied ?? DBNull.Value);
            command.Parameters.AddWithValue("@statusNote", (object?)statusNote ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetInternalAsync(connection, batchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetActiveBatchIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT batch_id FROM honua.migration_batch_runs
            WHERE status = 'running'
            ORDER BY started_at ASC;
            """;

        var ids = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<MigrationBatchRunRecord?> GetInternalAsync(
        NpgsqlConnection connection,
        string batchId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT batch_id, source_kind, source_url, source_display_name, status,
                   started_at, completed_at, total_children, succeeded_children,
                   failed_children, cancelled_children, apply_relationships,
                   relationships_applied, status_note
            FROM honua.migration_batch_runs
            WHERE batch_id = @batchId LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@batchId", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadBatch(reader);
    }

    private static async Task<MigrationBatchChildRecord?> GetChildInternalAsync(
        NpgsqlConnection connection,
        string batchId,
        int ordinal,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT batch_id, ordinal, source_resource_id, service_url, source_layer_id,
                   table_name, target_schema, service_name, depends_on::text, status,
                   job_id, published_layer_id, status_note, updated_at
            FROM honua.migration_batch_children
            WHERE batch_id = @batchId AND ordinal = @ordinal LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadChild(reader);
    }

    private static MigrationBatchRunRecord ReadBatch(NpgsqlDataReader reader)
    {
        var startedAt = reader.GetDateTime(reader.GetOrdinal("started_at"));
        var completedOrdinal = reader.GetOrdinal("completed_at");
        DateTimeOffset? completedAt = reader.IsDBNull(completedOrdinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(completedOrdinal), DateTimeKind.Utc));

        return new MigrationBatchRunRecord
        {
            BatchId = reader.GetString(reader.GetOrdinal("batch_id")),
            SourceKind = reader.GetString(reader.GetOrdinal("source_kind")),
            SourceUrl = reader.GetString(reader.GetOrdinal("source_url")),
            SourceDisplayName = reader.IsDBNull(reader.GetOrdinal("source_display_name"))
                ? null
                : reader.GetString(reader.GetOrdinal("source_display_name")),
            Status = BatchStatusFromText(reader.GetString(reader.GetOrdinal("status"))),
            StartedAt = new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)),
            CompletedAt = completedAt,
            TotalChildren = reader.GetInt32(reader.GetOrdinal("total_children")),
            SucceededChildren = reader.GetInt32(reader.GetOrdinal("succeeded_children")),
            FailedChildren = reader.GetInt32(reader.GetOrdinal("failed_children")),
            CancelledChildren = reader.GetInt32(reader.GetOrdinal("cancelled_children")),
            ApplyRelationships = reader.GetBoolean(reader.GetOrdinal("apply_relationships")),
            RelationshipsApplied = reader.GetBoolean(reader.GetOrdinal("relationships_applied")),
            StatusNote = reader.IsDBNull(reader.GetOrdinal("status_note"))
                ? null
                : reader.GetString(reader.GetOrdinal("status_note"))
        };
    }

    private static MigrationBatchChildRecord ReadChild(NpgsqlDataReader reader)
    {
        var dependsOrdinal = reader.GetOrdinal("depends_on");
        IReadOnlyList<string> dependsOn = [];
        if (!reader.IsDBNull(dependsOrdinal))
        {
            var json = reader.GetString(dependsOrdinal);
            if (!string.IsNullOrWhiteSpace(json))
            {
                dependsOn = JsonSerializer.Deserialize<string[]>(json, DependsOnOptions) ?? [];
            }
        }

        var jobOrdinal = reader.GetOrdinal("job_id");
        var layerOrdinal = reader.GetOrdinal("published_layer_id");
        var noteOrdinal = reader.GetOrdinal("status_note");
        var schemaOrdinal = reader.GetOrdinal("target_schema");
        var serviceOrdinal = reader.GetOrdinal("service_name");

        return new MigrationBatchChildRecord
        {
            BatchId = reader.GetString(reader.GetOrdinal("batch_id")),
            Ordinal = reader.GetInt32(reader.GetOrdinal("ordinal")),
            SourceResourceId = reader.GetString(reader.GetOrdinal("source_resource_id")),
            ServiceUrl = reader.GetString(reader.GetOrdinal("service_url")),
            SourceLayerId = reader.GetInt32(reader.GetOrdinal("source_layer_id")),
            TableName = reader.GetString(reader.GetOrdinal("table_name")),
            TargetSchema = reader.IsDBNull(schemaOrdinal) ? null : reader.GetString(schemaOrdinal),
            ServiceName = reader.IsDBNull(serviceOrdinal) ? null : reader.GetString(serviceOrdinal),
            DependsOn = dependsOn,
            Status = ChildStatusFromText(reader.GetString(reader.GetOrdinal("status"))),
            JobId = reader.IsDBNull(jobOrdinal) ? null : reader.GetString(jobOrdinal),
            PublishedLayerId = reader.IsDBNull(layerOrdinal) ? null : reader.GetInt32(layerOrdinal),
            StatusNote = reader.IsDBNull(noteOrdinal) ? null : reader.GetString(noteOrdinal),
            UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("updated_at")), DateTimeKind.Utc))
        };
    }

    internal static string BatchStatusToText(MigrationBatchRunStatus status) => status switch
    {
        MigrationBatchRunStatus.Running => "running",
        MigrationBatchRunStatus.Succeeded => "succeeded",
        MigrationBatchRunStatus.Failed => "failed",
        MigrationBatchRunStatus.Cancelled => "cancelled",
        MigrationBatchRunStatus.NeedsReview => "needs-review",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown migration batch status.")
    };

    internal static MigrationBatchRunStatus BatchStatusFromText(string text) => text switch
    {
        "running" => MigrationBatchRunStatus.Running,
        "succeeded" => MigrationBatchRunStatus.Succeeded,
        "failed" => MigrationBatchRunStatus.Failed,
        "cancelled" => MigrationBatchRunStatus.Cancelled,
        "needs-review" => MigrationBatchRunStatus.NeedsReview,
        _ => throw new InvalidOperationException($"Unknown migration batch status '{text}'.")
    };

    internal static string ChildStatusToText(MigrationBatchChildStatus status) => status switch
    {
        MigrationBatchChildStatus.Pending => "pending",
        MigrationBatchChildStatus.Running => "running",
        MigrationBatchChildStatus.Succeeded => "succeeded",
        MigrationBatchChildStatus.Failed => "failed",
        MigrationBatchChildStatus.NeedsReview => "needs-review",
        MigrationBatchChildStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown migration batch child status.")
    };

    internal static MigrationBatchChildStatus ChildStatusFromText(string text) => text switch
    {
        "pending" => MigrationBatchChildStatus.Pending,
        "running" => MigrationBatchChildStatus.Running,
        "succeeded" => MigrationBatchChildStatus.Succeeded,
        "failed" => MigrationBatchChildStatus.Failed,
        "needs-review" => MigrationBatchChildStatus.NeedsReview,
        "cancelled" => MigrationBatchChildStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown migration batch child status '{text}'.")
    };

    private static partial class Log
    {
        [LoggerMessage(7990, LogLevel.Information,
            "Migration batch catalog created batch '{BatchId}' (source={SourceKind}, children={ChildCount})")]
        public static partial void BatchCreated(ILogger logger, string batchId, string sourceKind, int childCount);
    }
}
