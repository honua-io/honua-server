// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed store for GitOps watch configurations and change records.
/// </summary>
internal sealed class PostgresGitOpsWatchStore : IGitOpsWatchStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _configTable;
    private readonly string _changeTable;

    public PostgresGitOpsWatchStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _configTable = Infrastructure.SchemaSearchPath.QualifyTable("gitops_watch_configs", schemaName);
        _changeTable = Infrastructure.SchemaSearchPath.QualifyTable("gitops_change_records", schemaName);
    }

    public async Task<GitOpsWatchConfig> UpsertConfigAsync(GitOpsWatchConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Use ON CONFLICT ((TRUE)) to target the singleton unique index,
        // ensuring only one config row can ever exist regardless of config_id.
        var sql = $"""
            INSERT INTO {_configTable} (config_id, repository_url, branch, manifest_path,
                poll_interval_seconds, approval_required, enabled, configured_by, created_at, updated_at)
            VALUES (@configId, @repoUrl, @branch, @manifestPath,
                @pollInterval, @approvalRequired, @enabled, @configuredBy, @createdAt, @updatedAt)
            ON CONFLICT ((TRUE)) DO UPDATE SET
                repository_url = EXCLUDED.repository_url,
                branch = EXCLUDED.branch,
                manifest_path = EXCLUDED.manifest_path,
                poll_interval_seconds = EXCLUDED.poll_interval_seconds,
                approval_required = EXCLUDED.approval_required,
                enabled = EXCLUDED.enabled,
                configured_by = EXCLUDED.configured_by,
                updated_at = EXCLUDED.updated_at
            RETURNING config_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@configId", config.ConfigId);
        command.Parameters.AddWithValue("@repoUrl", config.RepositoryUrl);
        command.Parameters.AddWithValue("@branch", config.Branch);
        command.Parameters.AddWithValue("@manifestPath", config.ManifestPath);
        command.Parameters.AddWithValue("@pollInterval", config.PollIntervalSeconds);
        command.Parameters.AddWithValue("@approvalRequired", config.ApprovalRequired);
        command.Parameters.AddWithValue("@enabled", config.Enabled);
        command.Parameters.AddWithValue("@configuredBy", (object?)config.ConfiguredBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", config.CreatedAt);
        command.Parameters.AddWithValue("@updatedAt", config.UpdatedAt);

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return config;
    }

    public async Task<GitOpsWatchConfig?> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT config_id, repository_url, branch, manifest_path,
                   poll_interval_seconds, approval_required, enabled,
                   last_known_commit_sha, last_polled_at, configured_by,
                   created_at, updated_at
            FROM {_configTable}
            ORDER BY created_at ASC
            LIMIT 1
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapConfig(reader);
    }

    public async Task<bool> DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default)
    {
        var sql = $"DELETE FROM {_configTable} WHERE config_id = @configId";

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@configId", configId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpdatePollStateAsync(Guid configId, string commitSha, DateTimeOffset polledAt, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE {_configTable}
            SET last_known_commit_sha = @commitSha, last_polled_at = @polledAt
            WHERE config_id = @configId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@configId", configId);
        command.Parameters.AddWithValue("@commitSha", commitSha);
        command.Parameters.AddWithValue("@polledAt", polledAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<GitOpsChangeRecord> CreateChangeRecordAsync(GitOpsChangeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sql = $"""
            INSERT INTO {_changeTable} (change_id, config_id, commit_sha, commit_message, commit_author,
                commit_timestamp, manifest_before, manifest_after, status, pending_approval_id,
                apply_summary, error_message, detected_at, applied_at)
            VALUES (@changeId, @configId, @commitSha, @commitMessage, @commitAuthor,
                @commitTimestamp, @manifestBefore::jsonb, @manifestAfter::jsonb, @status, @pendingApprovalId,
                @applySummary, @errorMessage, @detectedAt, @appliedAt)
            RETURNING change_id
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@changeId", record.ChangeId);
        command.Parameters.AddWithValue("@configId", record.ConfigId);
        command.Parameters.AddWithValue("@commitSha", record.CommitSha);
        command.Parameters.AddWithValue("@commitMessage", (object?)record.CommitMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@commitAuthor", (object?)record.CommitAuthor ?? DBNull.Value);
        command.Parameters.AddWithValue("@commitTimestamp", record.CommitTimestamp.HasValue ? record.CommitTimestamp.Value : DBNull.Value);
        command.Parameters.AddWithValue("@manifestBefore", NpgsqlDbType.Jsonb,
            record.ManifestBefore.HasValue ? record.ManifestBefore.Value.GetRawText() : (object)DBNull.Value);
        command.Parameters.AddWithValue("@manifestAfter", NpgsqlDbType.Jsonb, record.ManifestAfter.GetRawText());
        command.Parameters.AddWithValue("@status", record.Status.ToWireString());
        command.Parameters.AddWithValue("@pendingApprovalId", record.PendingApprovalId.HasValue ? record.PendingApprovalId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@applySummary", (object?)record.ApplySummary ?? DBNull.Value);
        command.Parameters.AddWithValue("@errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@detectedAt", record.DetectedAt);
        command.Parameters.AddWithValue("@appliedAt", record.AppliedAt.HasValue ? record.AppliedAt.Value : DBNull.Value);

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<GitOpsChangeRecord?> GetChangeRecordAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT change_id, config_id, commit_sha, commit_message, commit_author,
                   commit_timestamp, manifest_before, manifest_after, status,
                   pending_approval_id, apply_summary, error_message, detected_at, applied_at
            FROM {_changeTable}
            WHERE change_id = @changeId
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@changeId", changeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapChangeRecord(reader);
    }

    public async Task<IReadOnlyList<GitOpsChangeRecord>> ListChangeRecordsAsync(int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 1000);
        var effectiveOffset = Math.Max(0, offset);
        var sql = $"""
            SELECT change_id, config_id, commit_sha, commit_message, commit_author,
                   commit_timestamp, manifest_before, manifest_after, status,
                   pending_approval_id, apply_summary, error_message, detected_at, applied_at
            FROM {_changeTable}
            ORDER BY detected_at DESC
            LIMIT @limit OFFSET @offset
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", effectiveLimit);
        command.Parameters.AddWithValue("@offset", effectiveOffset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<GitOpsChangeRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapChangeRecord(reader));
        }

        return results;
    }

    private static GitOpsWatchConfig MapConfig(NpgsqlDataReader reader) => new()
    {
        ConfigId = reader.GetGuid(reader.GetOrdinal("config_id")),
        RepositoryUrl = reader.GetString(reader.GetOrdinal("repository_url")),
        Branch = reader.GetString(reader.GetOrdinal("branch")),
        ManifestPath = reader.GetString(reader.GetOrdinal("manifest_path")),
        PollIntervalSeconds = reader.GetInt32(reader.GetOrdinal("poll_interval_seconds")),
        ApprovalRequired = reader.GetBoolean(reader.GetOrdinal("approval_required")),
        Enabled = reader.GetBoolean(reader.GetOrdinal("enabled")),
        LastKnownCommitSha = reader.IsDBNull(reader.GetOrdinal("last_known_commit_sha")) ? null : reader.GetString(reader.GetOrdinal("last_known_commit_sha")),
        LastPolledAt = reader.IsDBNull(reader.GetOrdinal("last_polled_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_polled_at")),
        ConfiguredBy = reader.IsDBNull(reader.GetOrdinal("configured_by")) ? null : reader.GetString(reader.GetOrdinal("configured_by")),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at"))
    };

    private static GitOpsChangeRecord MapChangeRecord(NpgsqlDataReader reader)
    {
        var beforeOrdinal = reader.GetOrdinal("manifest_before");
        JsonElement? manifestBefore = null;
        if (!reader.IsDBNull(beforeOrdinal))
        {
            var beforeText = reader.GetString(beforeOrdinal);
            using var doc = JsonDocument.Parse(beforeText);
            manifestBefore = doc.RootElement.Clone();
        }

        var afterText = reader.GetString(reader.GetOrdinal("manifest_after"));
        using var afterDoc = JsonDocument.Parse(afterText);

        return new GitOpsChangeRecord
        {
            ChangeId = reader.GetGuid(reader.GetOrdinal("change_id")),
            ConfigId = reader.GetGuid(reader.GetOrdinal("config_id")),
            CommitSha = reader.GetString(reader.GetOrdinal("commit_sha")),
            CommitMessage = reader.IsDBNull(reader.GetOrdinal("commit_message")) ? null : reader.GetString(reader.GetOrdinal("commit_message")),
            CommitAuthor = reader.IsDBNull(reader.GetOrdinal("commit_author")) ? null : reader.GetString(reader.GetOrdinal("commit_author")),
            CommitTimestamp = reader.IsDBNull(reader.GetOrdinal("commit_timestamp")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("commit_timestamp")),
            ManifestBefore = manifestBefore,
            ManifestAfter = afterDoc.RootElement.Clone(),
            Status = GitOpsChangeStatusExtensions.ParseWireString(reader.GetString(reader.GetOrdinal("status"))),
            PendingApprovalId = reader.IsDBNull(reader.GetOrdinal("pending_approval_id")) ? null : reader.GetGuid(reader.GetOrdinal("pending_approval_id")),
            ApplySummary = reader.IsDBNull(reader.GetOrdinal("apply_summary")) ? null : reader.GetString(reader.GetOrdinal("apply_summary")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
            DetectedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("detected_at")),
            AppliedAt = reader.IsDBNull(reader.GetOrdinal("applied_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("applied_at"))
        };
    }

}
