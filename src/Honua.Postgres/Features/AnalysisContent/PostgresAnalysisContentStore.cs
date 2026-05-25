// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.AnalysisContent;

internal sealed class PostgresAnalysisContentStore : IAnalysisContentStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _itemsTable;
    private readonly string _versionsTable;
    private readonly string _artifactsTable;

    public PostgresAnalysisContentStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _itemsTable = SchemaSearchPath.QualifyTable("analysis_content_items", schemaName);
        _versionsTable = SchemaSearchPath.QualifyTable("analysis_content_versions", schemaName);
        _artifactsTable = SchemaSearchPath.QualifyTable("analysis_result_artifacts", schemaName);
    }

    public async Task<AnalysisContentItem> CreateItemAsync(
        AnalysisContentItem item,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(version);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertItemAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
            await InsertVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return item;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new AnalysisContentStoreConflictException(
                "Analysis content item conflicts with an existing record.",
                ex);
        }
    }

    public async Task<AnalysisContentItem?> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT item_id, kind, name, title, owner_id, visibility, current_version,
                   current_version_id, lifecycle, created_at, updated_at, created_by
            FROM {_itemsTable}
            WHERE item_id = @item_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@item_id", itemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadItem(reader)
            : null;
    }

    public async Task<AnalysisContentVersion> AddVersionAsync(
        string itemId,
        AnalysisContentVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var exists = await ItemExistsForUpdateAsync(connection, transaction, itemId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new KeyNotFoundException("Analysis content item was not found.");
        }

        try
        {
            await InsertVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            await UpdateCurrentVersionAsync(connection, transaction, version, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return version;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new AnalysisContentStoreConflictException(
                "Analysis content version conflicts with an existing record.",
                ex);
        }
    }

    public async Task<AnalysisContentVersion?> GetVersionAsync(
        string itemId,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        var sql = version.HasValue
            ? $"""
               SELECT version_body
               FROM {_versionsTable}
               WHERE item_id = @item_id AND version = @version
               """
            : $"""
               SELECT version_body
               FROM {_versionsTable}
               WHERE item_id = @item_id
               ORDER BY version DESC
               LIMIT 1
               """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@item_id", itemId);
        if (version.HasValue)
        {
            command.Parameters.AddWithValue("@version", version.Value);
        }

        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize(payload, AnalysisContentJsonContext.Default.AnalysisContentVersion);
    }

    public async Task<IReadOnlyList<AnalysisContentVersion>> ListVersionsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT version_body
            FROM {_versionsTable}
            WHERE item_id = @item_id
            ORDER BY version ASC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@item_id", itemId);

        var versions = new List<AnalysisContentVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var version = JsonSerializer.Deserialize(
                reader.GetString(0),
                AnalysisContentJsonContext.Default.AnalysisContentVersion);
            if (version is not null)
            {
                versions.Add(version);
            }
        }

        return versions;
    }

    public async Task<ResultArtifactRecord> UpsertArtifactAsync(
        ResultArtifactRecord artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var recordJson = JsonSerializer.Serialize(artifact, AnalysisContentJsonContext.Default.ResultArtifactRecord);
        var sql = $"""
            INSERT INTO {_artifactsTable}
                (artifact_id, result_package_id, job_id, source_item_id, source_version,
                 source_version_id, kind, retention_state, promotion_state, created_at, expires_at, artifact_body)
            VALUES
                (@artifact_id, @result_package_id, @job_id, @source_item_id, @source_version,
                 @source_version_id, @kind, @retention_state, @promotion_state, @created_at, @expires_at, @artifact_body)
            ON CONFLICT (artifact_id) DO UPDATE SET
                result_package_id = EXCLUDED.result_package_id,
                job_id = EXCLUDED.job_id,
                source_item_id = EXCLUDED.source_item_id,
                source_version = EXCLUDED.source_version,
                source_version_id = EXCLUDED.source_version_id,
                kind = EXCLUDED.kind,
                retention_state = EXCLUDED.retention_state,
                promotion_state = EXCLUDED.promotion_state,
                expires_at = EXCLUDED.expires_at,
                artifact_body = EXCLUDED.artifact_body
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@artifact_id", artifact.ArtifactId);
        command.Parameters.AddWithValue("@result_package_id", artifact.ResultPackageId);
        command.Parameters.AddWithValue("@job_id", artifact.JobId);
        command.Parameters.AddWithValue("@source_item_id", artifact.SourceItemId);
        command.Parameters.AddWithValue("@source_version", artifact.SourceVersion);
        command.Parameters.AddWithValue("@source_version_id", artifact.SourceVersionId);
        command.Parameters.AddWithValue("@kind", artifact.Kind.ToString());
        command.Parameters.AddWithValue("@retention_state", artifact.RetentionState.ToString());
        command.Parameters.AddWithValue("@promotion_state", artifact.PromotionState.ToString());
        command.Parameters.AddWithValue("@created_at", artifact.CreatedAt);
        command.Parameters.AddWithValue("@expires_at", (object?)artifact.ExpiresAt ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("@artifact_body", NpgsqlDbType.Jsonb) { Value = recordJson });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    public async Task<ResultArtifactRecord?> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT artifact_body
            FROM {_artifactsTable}
            WHERE artifact_id = @artifact_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@artifact_id", artifactId);

        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize(payload, AnalysisContentJsonContext.Default.ResultArtifactRecord);
    }

    public async Task<IReadOnlyList<ResultArtifactRecord>> ListArtifactsForJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT artifact_body
            FROM {_artifactsTable}
            WHERE job_id = @job_id
            ORDER BY created_at ASC, artifact_id ASC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@job_id", jobId);

        var artifacts = new List<ResultArtifactRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var artifact = JsonSerializer.Deserialize(
                reader.GetString(0),
                AnalysisContentJsonContext.Default.ResultArtifactRecord);
            if (artifact is not null)
            {
                artifacts.Add(artifact);
            }
        }

        return artifacts;
    }

    private async Task InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AnalysisContentItem item,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_itemsTable}
                (item_id, kind, name, title, owner_id, visibility, current_version,
                 current_version_id, lifecycle, created_at, updated_at, created_by)
            VALUES
                (@item_id, @kind, @name, @title, @owner_id, @visibility, @current_version,
                 @current_version_id, @lifecycle, @created_at, @updated_at, @created_by)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", item.ItemId);
        command.Parameters.AddWithValue("@kind", item.Kind.ToString());
        command.Parameters.AddWithValue("@name", item.Name);
        command.Parameters.AddWithValue("@title", (object?)item.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("@owner_id", (object?)item.OwnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@visibility", item.Visibility);
        command.Parameters.AddWithValue("@current_version", item.CurrentVersion);
        command.Parameters.AddWithValue("@current_version_id", item.CurrentVersionId);
        command.Parameters.AddWithValue("@lifecycle", item.Lifecycle.ToString());
        command.Parameters.AddWithValue("@created_at", item.CreatedAt);
        command.Parameters.AddWithValue("@updated_at", item.UpdatedAt);
        command.Parameters.AddWithValue("@created_by", (object?)item.CreatedBy ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AnalysisContentVersion version,
        CancellationToken cancellationToken)
    {
        var versionJson = JsonSerializer.Serialize(
            version,
            AnalysisContentJsonContext.Default.AnalysisContentVersion);
        var sql = $"""
            INSERT INTO {_versionsTable}
                (version_id, item_id, version, kind, content_hash, created_at, created_by, version_body)
            VALUES
                (@version_id, @item_id, @version, @kind, @content_hash, @created_at, @created_by, @version_body)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@version_id", version.VersionId);
        command.Parameters.AddWithValue("@item_id", version.ItemId);
        command.Parameters.AddWithValue("@version", version.Version);
        command.Parameters.AddWithValue("@kind", version.Kind.ToString());
        command.Parameters.AddWithValue("@content_hash", version.ContentHash);
        command.Parameters.AddWithValue("@created_at", version.CreatedAt);
        command.Parameters.AddWithValue("@created_by", (object?)version.CreatedBy ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("@version_body", NpgsqlDbType.Jsonb) { Value = versionJson });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ItemExistsForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string itemId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT 1
            FROM {_itemsTable}
            WHERE item_id = @item_id
            FOR UPDATE
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@item_id", itemId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private async Task UpdateCurrentVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AnalysisContentVersion version,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE {_itemsTable}
            SET current_version = @current_version,
                current_version_id = @current_version_id,
                updated_at = @updated_at
            WHERE item_id = @item_id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@current_version", version.Version);
        command.Parameters.AddWithValue("@current_version_id", version.VersionId);
        command.Parameters.AddWithValue("@updated_at", version.CreatedAt);
        command.Parameters.AddWithValue("@item_id", version.ItemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AnalysisContentItem ReadItem(NpgsqlDataReader reader)
        => new()
        {
            ItemId = reader.GetString(0),
            Kind = Enum.Parse<AnalysisContentKind>(reader.GetString(1), ignoreCase: true),
            Name = reader.GetString(2),
            Title = reader.IsDBNull(3) ? null : reader.GetString(3),
            OwnerId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Visibility = reader.GetString(5),
            CurrentVersion = reader.GetInt32(6),
            CurrentVersionId = reader.GetString(7),
            Lifecycle = Enum.Parse<AnalysisContentLifecycle>(reader.GetString(8), ignoreCase: true),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
            CreatedBy = reader.IsDBNull(11) ? null : reader.GetString(11)
        };
}
