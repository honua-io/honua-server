// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Geoprocessing;

internal sealed partial class PostgresWorkspaceStore
{
    private const string ArtifactColumns = "artifact_id, workspace_id, kind, label, state, uri, content_type, size_bytes, created_at, metadata";

    public Task<Artifact> CreateAsync(Artifact artifact, CancellationToken cancellationToken = default)
        => CreateArtifactWithQuotaAsync(artifact, WorkspaceQuota.Default, cancellationToken);

    public async Task<Artifact> CreateArtifactWithQuotaAsync(Artifact artifact, WorkspaceQuota quota, CancellationToken cancellationToken = default)
        => await WriteArtifactAsync(artifact, false, quota, null, false, cancellationToken).ConfigureAwait(false)
            ?? throw new ArtifactAlreadyExistsException(artifact.WorkspaceId, artifact.Label);

    public Task<Artifact?> AddOrReplaceAsync(Artifact artifact, bool overwrite, CancellationToken cancellationToken = default)
        => AddOrReplaceWithQuotaAsync(artifact, overwrite, WorkspaceQuota.Default, cancellationToken);

    public Task<Artifact?> AddOrReplaceWithQuotaAsync(Artifact artifact, bool overwrite, WorkspaceQuota quota, CancellationToken cancellationToken = default)
        => WriteArtifactAsync(artifact, overwrite, quota, null, true, cancellationToken);

    public Task<Artifact?> PublishAsync(Artifact artifact, bool overwrite, WorkspaceQuota quota,
        Func<CancellationToken, Task<bool>> publishReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishReference);
        return WriteArtifactAsync(artifact, overwrite, quota, publishReference, true, cancellationToken);
    }

    private async Task<Artifact?> WriteArtifactAsync(Artifact artifact, bool overwrite, WorkspaceQuota quota,
        Func<CancellationToken, Task<bool>>? publishReference, bool namedOutput, CancellationToken cancellationToken)
    {
        ValidateArtifact(artifact);
        ArgumentNullException.ThrowIfNull(quota);
        if (namedOutput && artifact.State != ArtifactLifecycleState.Available)
        {
            throw new ArgumentException("A named output must be Available.", nameof(artifact));
        }
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string owner;
        await using (var lookup = new NpgsqlCommand($"SELECT owner_id FROM {_workspaces} WHERE workspace_id = @id", connection, transaction))
        {
            lookup.Parameters.AddWithValue("id", artifact.WorkspaceId);
            owner = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("Workspace not found.");
        }
        // Owner precedes workspace, matching workspace creation. Different workspace
        // labels cannot race past the same owner's aggregate artifact limits.
        await LockOwnerAsync(connection, transaction, owner, cancellationToken).ConfigureAwait(false);
        await LockWritableWorkspaceAsync(connection, transaction, artifact.WorkspaceId, owner, cancellationToken).ConfigureAwait(false);
        string? existing;
        await using (var command = new NpgsqlCommand($"SELECT artifact_id FROM {_artifacts} WHERE workspace_id = @workspace AND label_key = @label AND state = @available", connection, transaction))
        {
            command.Parameters.AddWithValue("workspace", artifact.WorkspaceId);
            command.Parameters.AddWithValue("label", artifact.Label.ToUpperInvariant());
            command.Parameters.AddWithValue("available", (int)ArtifactLifecycleState.Available);
            existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        var ownedRetry = publishReference is not null && existing == artifact.ArtifactId;
        if (existing is not null && artifact.State == ArtifactLifecycleState.Available
            && (!namedOutput || (!overwrite && !ownedRetry)))
        {
            if (publishReference is not null || !namedOutput)
            {
                throw new ArtifactAlreadyExistsException(artifact.WorkspaceId, artifact.Label);
            }
            return null;
        }
        if (!namedOutput)
        {
            existing = null;
        }
        await CheckArtifactQuotaAsync(connection, transaction, owner, artifact, existing, quota, cancellationToken).ConfigureAwait(false);
        // Keep the storage gate through durable acceptance: an accepted old writer
        // cannot wait outside the gate then overwrite a newer attempt's output.
        // Redis acceptance and the PostgreSQL commit are not a distributed transaction;
        // stable operation/output identities permit recovery after a commit failure.
        if (publishReference is not null && !await publishReference(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (existing is not null)
        {
            await using var deletion = new NpgsqlCommand($"DELETE FROM {_artifacts} WHERE artifact_id = @id", connection, transaction);
            deletion.Parameters.AddWithValue("id", existing);
            await deletion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertArtifactAsync(connection, transaction, artifact, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    private async Task CheckArtifactQuotaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string owner, Artifact artifact, string? replacedId, WorkspaceQuota quota, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT COUNT(*), COALESCE(SUM(a.size_bytes), 0)
            FROM {_artifacts} a JOIN {_workspaces} w ON a.workspace_id = w.workspace_id
            WHERE w.owner_id = @owner AND w.state = @active
              AND (w.expires_at IS NULL OR w.expires_at > @now)
              AND a.state <> @deleted AND a.artifact_id IS DISTINCT FROM @replaced
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("active", (int)WorkspaceLifecycleState.Active);
        command.Parameters.AddWithValue("now", _clock.GetUtcNow());
        command.Parameters.AddWithValue("deleted", (int)ArtifactLifecycleState.Deleted);
        command.Parameters.Add(Text("replaced", replacedId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = reader.GetInt64(0);
        var bytes = reader.GetFieldValue<decimal>(1);
        var counted = artifact.State != ArtifactLifecycleState.Deleted;
        if ((quota.MaxArtifactCount is { } maxCount && count + (counted ? 1 : 0) > maxCount)
            || (quota.MaxStorageBytes is { } maxBytes && bytes + (counted ? artifact.SizeBytes : 0) > maxBytes))
        {
            throw new WorkspaceQuotaExceededException("The workspace artifact count or recorded storage limit has been reached.");
        }
    }

    async Task<Artifact?> IArtifactStore.GetAsync(string artifactId, CancellationToken cancellationToken)
    {
        var rows = await ReadArtifactsAsync("artifact_id = @id", Text("id", artifactId), cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<IReadOnlyList<Artifact>> ListByWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => ReadArtifactsAsync("workspace_id = @workspace ORDER BY created_at, artifact_id", Text("workspace", workspaceId), cancellationToken);

    public Task<bool> TransitionStateAsync(string artifactId, ArtifactLifecycleState newState, CancellationToken cancellationToken = default)
        => MutateArtifactAsync(artifactId, newState == ArtifactLifecycleState.Deleted ? null : newState, cancellationToken);

    // This provider owns durable reference records, not the external resources they
    // identify. Never follow a caller-supplied URI to delete a file, layer or cloud object.
    Task<bool> IArtifactStore.DeleteAsync(string artifactId, CancellationToken cancellationToken)
        => MutateArtifactAsync(artifactId, null, cancellationToken);

    private async Task<bool> MutateArtifactAsync(string artifactId, ArtifactLifecycleState? state, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string? workspaceId;
        await using (var lookup = new NpgsqlCommand($"SELECT workspace_id FROM {_artifacts} WHERE artifact_id = @id", connection, transaction))
        {
            lookup.Parameters.AddWithValue("id", artifactId);
            workspaceId = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        if (workspaceId is null)
        {
            return false;
        }
        // All artifact mutations use workspace-then-artifact lock ordering, including
        // promotion and cleanup, so they cannot invalidate an overwrite's collision check.
        await using (var gate = new NpgsqlCommand($"SELECT workspace_id FROM {_workspaces} WHERE workspace_id = @id FOR UPDATE", connection, transaction))
        {
            gate.Parameters.AddWithValue("id", workspaceId);
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var sql = state.HasValue
            ? $"UPDATE {_artifacts} SET state = @state WHERE artifact_id = @id AND workspace_id = @workspace"
            : $"DELETE FROM {_artifacts} WHERE artifact_id = @id AND workspace_id = @workspace";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", artifactId);
        command.Parameters.AddWithValue("workspace", workspaceId);
        if (state.HasValue)
        {
            command.Parameters.AddWithValue("state", (int)state.Value);
        }
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private async Task LockWritableWorkspaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string workspaceId, string ownerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT state, expires_at FROM {_workspaces} WHERE workspace_id = @id AND owner_id = @owner FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", workspaceId);
        command.Parameters.AddWithValue("owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt32(0) != (int)WorkspaceLifecycleState.Active
            || (!reader.IsDBNull(1) && reader.GetFieldValue<DateTimeOffset>(1) <= _clock.GetUtcNow()))
        {
            throw new InvalidOperationException("Only an active, unexpired workspace accepts artifacts.");
        }
    }

    private async Task InsertArtifactAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Artifact artifact, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_artifacts} ({ArtifactColumns}, label_key)
            VALUES (@id,@workspace,@kind,@label,@state,@uri,@content_type,@bytes,@created,@metadata,@label_key)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", artifact.ArtifactId);
        command.Parameters.AddWithValue("workspace", artifact.WorkspaceId);
        command.Parameters.AddWithValue("kind", (int)artifact.Kind);
        command.Parameters.AddWithValue("label", artifact.Label);
        command.Parameters.AddWithValue("label_key", artifact.Label.ToUpperInvariant());
        command.Parameters.AddWithValue("state", (int)artifact.State);
        command.Parameters.Add(Text("uri", artifact.Uri));
        command.Parameters.Add(Text("content_type", artifact.ContentType));
        command.Parameters.AddWithValue("bytes", artifact.SizeBytes);
        command.Parameters.AddWithValue("created", artifact.CreatedAt);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(new Dictionary<string, string>(artifact.Metadata), WorkspaceStoreJsonContext.Default.DictionaryStringString));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Artifact>> ReadArtifactsAsync(string predicate, NpgsqlParameter parameter, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {ArtifactColumns} FROM {_artifacts} WHERE {predicate}", connection);
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<Artifact>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new Artifact
            {
                ArtifactId = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                Kind = (ArtifactKind)reader.GetInt32(2),
                Label = reader.GetString(3),
                State = (ArtifactLifecycleState)reader.GetInt32(4),
                Uri = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContentType = reader.IsDBNull(6) ? null : reader.GetString(6),
                SizeBytes = reader.GetInt64(7),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                Metadata = JsonSerializer.Deserialize(reader.GetString(9), WorkspaceStoreJsonContext.Default.DictionaryStringString)
                    ?? throw new InvalidOperationException("Artifact metadata was not an object.")
            });
        }
        return rows;
    }

    private static void ValidateArtifact(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Label);
        ArgumentOutOfRangeException.ThrowIfNegative(artifact.SizeBytes);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WorkspaceStoreJsonContext : JsonSerializerContext;
