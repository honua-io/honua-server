// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Geoprocessing;

internal sealed partial class PostgresWorkspaceStore : IWorkspaceStore, IArtifactStore, IAtomicWorkspaceStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connections;
    private readonly string _workspaces;
    private readonly string _artifacts;
    private readonly TimeProvider _clock;
    private const string WorkspaceColumns = "workspace_id, kind, label, owner_id, scope_id, state, uri, created_at, expires_at";

    public PostgresWorkspaceStore(IAdoNetDatabaseConnectionProvider connections, string? schemaName = null, TimeProvider? clock = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _workspaces = SchemaSearchPath.QualifyTable("gp_workspaces", schemaName);
        _artifacts = SchemaSearchPath.QualifyTable("gp_workspace_artifacts", schemaName);
        _clock = clock ?? TimeProvider.System;
    }

    public Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => CreateWithQuotaAsync(workspace, cancellationToken: cancellationToken);

    public async Task<Workspace> CreateWithQuotaAsync(Workspace workspace, int? maxWorkspaceCount = null, CancellationToken cancellationToken = default)
    {
        ValidateWorkspace(workspace);
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockOwnerAsync(connection, transaction, workspace.OwnerId, cancellationToken).ConfigureAwait(false);
        await CheckWorkspaceQuotaAsync(connection, transaction, workspace, maxWorkspaceCount, cancellationToken).ConfigureAwait(false);
        await InsertWorkspaceAsync(connection, transaction, workspace, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return workspace with { Artifacts = [], StorageBytes = 0 };
    }

    public async Task<Workspace> GetOrCreateNamedAsync(Workspace proposal, int? maxWorkspaceCount = null, CancellationToken cancellationToken = default)
    {
        ValidateWorkspace(proposal);
        if (proposal.State != WorkspaceLifecycleState.Active || proposal.IsExpired(_clock.GetUtcNow()))
        {
            throw new ArgumentException("A named workspace proposal must be active and unexpired.", nameof(proposal));
        }
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockOwnerAsync(connection, transaction, proposal.OwnerId, cancellationToken).ConfigureAwait(false);

        Workspace? existing;
        await using (var command = new NpgsqlCommand($"""
            SELECT {WorkspaceColumns} FROM {_workspaces}
            WHERE owner_id = @owner AND scope_id IS NOT DISTINCT FROM @scope AND label = @label
                AND state = @active AND (expires_at IS NULL OR expires_at > @now)
            ORDER BY created_at DESC, workspace_id LIMIT 1 FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("owner", proposal.OwnerId);
            command.Parameters.Add(Text("scope", proposal.ScopeId));
            command.Parameters.AddWithValue("label", proposal.Label);
            command.Parameters.AddWithValue("active", (int)WorkspaceLifecycleState.Active);
            command.Parameters.AddWithValue("now", _clock.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            existing = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadWorkspace(reader) : null;
        }

        if (existing is null)
        {
            await CheckWorkspaceQuotaAsync(connection, transaction, proposal, maxWorkspaceCount, cancellationToken).ConfigureAwait(false);
            await InsertWorkspaceAsync(connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return existing ?? proposal with { Artifacts = [], StorageBytes = 0 };
    }

    private async Task LockOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string ownerId, CancellationToken cancellationToken)
    {
        // All labels and optional scopes for an owner share the same count quota.
        // Hash collisions only serialize unrelated owners; SQL still checks exact identity.
        var identity = string.Create(CultureInfo.InvariantCulture, $"{_workspaces.Length}:{_workspaces}{ownerId.Length}:{ownerId}");
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0))", connection, transaction);
        gate.Parameters.AddWithValue("identity", identity);
        await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckWorkspaceQuotaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Workspace proposal, int? maxWorkspaceCount, CancellationToken cancellationToken)
    {
        var limit = maxWorkspaceCount ?? WorkspaceQuota.Default.MaxWorkspaceCount!.Value;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (proposal.State != WorkspaceLifecycleState.Active || proposal.IsExpired(_clock.GetUtcNow()))
        {
            return;
        }
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM {_workspaces}
            WHERE owner_id = @owner AND state = @active AND (expires_at IS NULL OR expires_at > @now)
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", proposal.OwnerId);
        command.Parameters.AddWithValue("active", (int)WorkspaceLifecycleState.Active);
        command.Parameters.AddWithValue("now", _clock.GetUtcNow());
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (count >= limit)
        {
            throw new WorkspaceQuotaExceededException();
        }
    }

    public async Task<Workspace?> GetAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var rows = await ReadWorkspacesAsync("workspace_id = @id", cancellationToken, Text("id", workspaceId)).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }
        var artifacts = await ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return rows[0] with { Artifacts = artifacts, StorageBytes = artifacts.Where(a => a.State != ArtifactLifecycleState.Deleted).Sum(a => a.SizeBytes) };
    }

    public Task<IReadOnlyList<Workspace>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
        => ReadWorkspacesAsync("owner_id = @owner ORDER BY created_at, workspace_id", cancellationToken, Text("owner", ownerId));

    public Task<IReadOnlyList<Workspace>> ListExpiredAsync(DateTimeOffset threshold, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        return ReadWorkspacesAsync("expires_at <= @threshold ORDER BY expires_at, workspace_id LIMIT @limit", cancellationToken,
            new NpgsqlParameter("threshold", threshold), new NpgsqlParameter("limit", maxCount));
    }

    public Task<bool> TransitionStateAsync(string workspaceId, WorkspaceLifecycleState newState, CancellationToken cancellationToken = default)
        => UpdateAsync($"UPDATE {_workspaces} SET state = @value WHERE workspace_id = @id", workspaceId, (int)newState, cancellationToken);

    public Task<bool> ExtendExpirationAsync(string workspaceId, DateTimeOffset newExpiration, CancellationToken cancellationToken = default)
        => UpdateAsync($"UPDATE {_workspaces} SET expires_at = @value WHERE workspace_id = @id", workspaceId, newExpiration, cancellationToken);

    public Task<bool> DeleteAsync(string workspaceId, CancellationToken cancellationToken = default)
        => UpdateAsync($"DELETE FROM {_workspaces} WHERE workspace_id = @id AND NOT EXISTS (SELECT 1 FROM {_artifacts} WHERE workspace_id = @id)", workspaceId, null, cancellationToken);

    public async Task<WorkspaceUsageSummary> GetUsageSummaryAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT count(DISTINCT w.workspace_id)::integer, count(a.artifact_id)::integer, COALESCE(sum(a.size_bytes), 0)::bigint
            FROM {_workspaces} w LEFT JOIN {_artifacts} a ON a.workspace_id = w.workspace_id AND a.state <> @deleted
            WHERE w.owner_id = @owner AND w.state = @active AND (w.expires_at IS NULL OR w.expires_at > @now)
            """, connection);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("active", (int)WorkspaceLifecycleState.Active);
        command.Parameters.AddWithValue("deleted", (int)ArtifactLifecycleState.Deleted);
        command.Parameters.AddWithValue("now", _clock.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceUsageSummary { ActiveWorkspaceCount = reader.GetInt32(0), TotalArtifactCount = reader.GetInt32(1), TotalStorageBytes = reader.GetInt64(2) };
    }

    private async Task InsertWorkspaceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Workspace workspace, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {_workspaces} ({WorkspaceColumns}) VALUES (@id,@kind,@label,@owner,@scope,@state,@uri,@created,@expires)", connection, transaction);
        command.Parameters.AddWithValue("id", workspace.WorkspaceId);
        command.Parameters.AddWithValue("kind", (int)workspace.Kind);
        command.Parameters.AddWithValue("label", workspace.Label);
        command.Parameters.AddWithValue("owner", workspace.OwnerId);
        command.Parameters.Add(Text("scope", workspace.ScopeId));
        command.Parameters.AddWithValue("state", (int)workspace.State);
        command.Parameters.Add(Text("uri", workspace.Uri));
        command.Parameters.AddWithValue("created", workspace.CreatedAt);
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = (object?)workspace.ExpiresAt ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Workspace>> ReadWorkspacesAsync(string predicate, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {WorkspaceColumns},
                COALESCE((SELECT sum(a.size_bytes) FROM {_artifacts} a
                    WHERE a.workspace_id = w.workspace_id AND a.state <> @deleted), 0)::bigint
            FROM {_workspaces} w WHERE {predicate}
            """, connection);
        command.Parameters.AddRange(parameters);
        command.Parameters.AddWithValue("deleted", (int)ArtifactLifecycleState.Deleted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<Workspace>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadWorkspace(reader) with { StorageBytes = reader.GetInt64(9) });
        }
        return rows;
    }

    private static Workspace ReadWorkspace(NpgsqlDataReader reader) => new()
    {
        WorkspaceId = reader.GetString(0),
        Kind = (WorkspaceKind)reader.GetInt32(1),
        Label = reader.GetString(2),
        OwnerId = reader.GetString(3),
        ScopeId = reader.IsDBNull(4) ? null : reader.GetString(4),
        State = (WorkspaceLifecycleState)reader.GetInt32(5),
        Uri = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
        ExpiresAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)
    };

    private async Task<bool> UpdateAsync(string sql, string id, object? value, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        if (value is not null)
        {
            command.Parameters.AddWithValue("value", value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
    }

    private static NpgsqlParameter Text(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static void ValidateWorkspace(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.OwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.Label);
        if (workspace.Artifacts.Count != 0 || workspace.StorageBytes != 0)
        {
            throw new ArgumentException("Create artifacts through the artifact store after creating their workspace.", nameof(workspace));
        }
    }
}
