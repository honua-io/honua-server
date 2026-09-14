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

    public async Task<Artifact> CreateAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockWritableWorkspaceAsync(connection, transaction, artifact.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await InsertArtifactAsync(connection, transaction, artifact, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    public async Task<Artifact?> AddOrReplaceAsync(Artifact artifact, bool overwrite, CancellationToken cancellationToken = default)
    {
        ValidateArtifact(artifact);
        if (artifact.State != ArtifactLifecycleState.Available)
        {
            throw new ArgumentException("A named output must be Available.", nameof(artifact));
        }
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockWritableWorkspaceAsync(connection, transaction, artifact.WorkspaceId, cancellationToken).ConfigureAwait(false);
        string? existing;
        await using (var command = new NpgsqlCommand($"SELECT artifact_id FROM {_artifacts} WHERE workspace_id = @workspace AND label_key = @label AND state = @available", connection, transaction))
        {
            command.Parameters.AddWithValue("workspace", artifact.WorkspaceId);
            command.Parameters.AddWithValue("label", artifact.Label.ToUpperInvariant());
            command.Parameters.AddWithValue("available", (int)ArtifactLifecycleState.Available);
            existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        if (existing is not null)
        {
            if (!overwrite)
            {
                return null;
            }
            // Both changes commit together. An insert failure restores the old record
            // when the transaction is disposed; no intermediate deletion is visible.
            await using var deletion = new NpgsqlCommand($"DELETE FROM {_artifacts} WHERE artifact_id = @id", connection, transaction);
            deletion.Parameters.AddWithValue("id", existing);
            await deletion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertArtifactAsync(connection, transaction, artifact, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return artifact;
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

    private async Task LockWritableWorkspaceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string workspaceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT state, expires_at FROM {_workspaces} WHERE workspace_id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", workspaceId);
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
