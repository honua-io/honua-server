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
/// PostgreSQL-backed store for manifest pending approval changes.
/// </summary>
internal sealed class PostgresManifestPendingChangeStore : IManifestPendingChangeStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresManifestPendingChangeStore(
        IDatabaseConnectionProvider connectionProvider,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        _connectionProvider = connectionProvider;
        _table = Infrastructure.SchemaSearchPath.QualifyTable("manifest_pending_changes", schemaName);
    }

    public async Task<ManifestPendingChange> CreateAsync(ManifestPendingChange pendingChange, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pendingChange);

        var sql = $"""
            INSERT INTO {_table} (pending_id, manifest_snapshot, manifest_hash, status,
                requested_by, requested_reason, dry_run, prune, resource_count, created_at, expires_at)
            VALUES (@pendingId, @snapshot::jsonb, @hash, @status, @requestedBy, @requestedReason,
                @dryRun, @prune, @resourceCount, @createdAt, @expiresAt)
            RETURNING pending_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pendingId", pendingChange.PendingId);
        command.Parameters.AddWithValue("@snapshot", NpgsqlDbType.Jsonb, pendingChange.ManifestSnapshot.GetRawText());
        command.Parameters.AddWithValue("@hash", pendingChange.ManifestHash);
        command.Parameters.AddWithValue("@status", MapStatusToString(pendingChange.Status));
        command.Parameters.AddWithValue("@requestedBy", (object?)pendingChange.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@requestedReason", (object?)pendingChange.RequestedReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@dryRun", pendingChange.DryRun);
        command.Parameters.AddWithValue("@prune", pendingChange.Prune);
        command.Parameters.AddWithValue("@resourceCount", pendingChange.ResourceCount);
        command.Parameters.AddWithValue("@createdAt", pendingChange.CreatedAt);
        command.Parameters.AddWithValue("@expiresAt", (object?)pendingChange.ExpiresAt ?? DBNull.Value);

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return pendingChange;
    }

    public async Task<ManifestPendingChange?> GetAsync(Guid pendingId, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT pending_id, manifest_snapshot, manifest_hash, status,
                   requested_by, requested_reason, decision_by, decision_reason,
                   dry_run, prune, resource_count, created_at, decided_at, expires_at
            FROM {_table}
            WHERE pending_id = @pendingId
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pendingId", pendingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapPendingChange(reader);
    }

    public async Task<IReadOnlyList<ManifestPendingChange>> ListAsync(
        ManifestApprovalStatus? status = null,
        int limit = 200,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 1000);
        var effectiveOffset = Math.Max(0, offset);
        var sql = $"""
            SELECT pending_id, manifest_snapshot, manifest_hash, status,
                   requested_by, requested_reason, decision_by, decision_reason,
                   dry_run, prune, resource_count, created_at, decided_at, expires_at
            FROM {_table}
            """;

        if (status.HasValue)
        {
            sql += " WHERE status = @status";
        }

        sql += " ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", effectiveLimit);
        command.Parameters.AddWithValue("@offset", effectiveOffset);
        if (status.HasValue)
        {
            command.Parameters.AddWithValue("@status", MapStatusToString(status.Value));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ManifestPendingChange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapPendingChange(reader));
        }

        return results;
    }

    public async Task<bool> UpdateDecisionAsync(
        Guid pendingId,
        ManifestApprovalStatus status,
        string? decisionBy,
        string? decisionReason,
        ManifestApprovalStatus expectedCurrentStatus = ManifestApprovalStatus.Pending,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            UPDATE {_table}
            SET status = @status, decision_by = @decisionBy, decision_reason = @decisionReason,
                decided_at = @decidedAt
            WHERE pending_id = @pendingId AND status = @expectedStatus
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pendingId", pendingId);
        command.Parameters.AddWithValue("@status", MapStatusToString(status));
        command.Parameters.AddWithValue("@expectedStatus", MapStatusToString(expectedCurrentStatus));
        command.Parameters.AddWithValue("@decisionBy", (object?)decisionBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@decisionReason", (object?)decisionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@decidedAt", status == ManifestApprovalStatus.Pending ? DBNull.Value : DateTimeOffset.UtcNow);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlyList<ManifestPendingChange>> ListExpiredAsync(
        DateTimeOffset asOf,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 1000);
        var sql = $"""
            SELECT pending_id, manifest_snapshot, manifest_hash, status,
                   requested_by, requested_reason, decision_by, decision_reason,
                   dry_run, prune, resource_count, created_at, decided_at, expires_at
            FROM {_table}
            WHERE status = 'pending' AND expires_at IS NOT NULL AND expires_at <= @asOf
            ORDER BY expires_at ASC
            LIMIT @limit
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@asOf", asOf);
        command.Parameters.AddWithValue("@limit", effectiveLimit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ManifestPendingChange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapPendingChange(reader));
        }

        return results;
    }

    private static ManifestPendingChange MapPendingChange(NpgsqlDataReader reader)
    {
        var snapshotText = reader.GetString(reader.GetOrdinal("manifest_snapshot"));
        using var snapshotDoc = JsonDocument.Parse(snapshotText);
        return new ManifestPendingChange
        {
            PendingId = reader.GetGuid(reader.GetOrdinal("pending_id")),
            ManifestSnapshot = snapshotDoc.RootElement.Clone(),
            ManifestHash = reader.GetString(reader.GetOrdinal("manifest_hash")),
            Status = ParseStatus(reader.GetString(reader.GetOrdinal("status"))),
            RequestedBy = reader.IsDBNull(reader.GetOrdinal("requested_by")) ? null : reader.GetString(reader.GetOrdinal("requested_by")),
            RequestedReason = reader.IsDBNull(reader.GetOrdinal("requested_reason")) ? null : reader.GetString(reader.GetOrdinal("requested_reason")),
            DecisionBy = reader.IsDBNull(reader.GetOrdinal("decision_by")) ? null : reader.GetString(reader.GetOrdinal("decision_by")),
            DecisionReason = reader.IsDBNull(reader.GetOrdinal("decision_reason")) ? null : reader.GetString(reader.GetOrdinal("decision_reason")),
            DryRun = reader.GetBoolean(reader.GetOrdinal("dry_run")),
            Prune = reader.GetBoolean(reader.GetOrdinal("prune")),
            ResourceCount = reader.GetInt32(reader.GetOrdinal("resource_count")),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            DecidedAt = reader.IsDBNull(reader.GetOrdinal("decided_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("decided_at")),
            ExpiresAt = reader.IsDBNull(reader.GetOrdinal("expires_at")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at"))
        };
    }

    private static string MapStatusToString(ManifestApprovalStatus status) => status switch
    {
        ManifestApprovalStatus.Pending => "pending",
        ManifestApprovalStatus.Applying => "applying",
        ManifestApprovalStatus.Approved => "approved",
        ManifestApprovalStatus.Rejected => "rejected",
        ManifestApprovalStatus.Expired => "expired",
        _ => "pending"
    };

    private static ManifestApprovalStatus ParseStatus(string status) => status switch
    {
        "applying" => ManifestApprovalStatus.Applying,
        "approved" => ManifestApprovalStatus.Approved,
        "rejected" => ManifestApprovalStatus.Rejected,
        "expired" => ManifestApprovalStatus.Expired,
        _ => ManifestApprovalStatus.Pending
    };
}
