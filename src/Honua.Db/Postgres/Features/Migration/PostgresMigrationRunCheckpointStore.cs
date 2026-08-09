// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// PostgreSQL implementation of <see cref="IMigrationRunCheckpointStore"/> backed by
/// <c>honua.migration_run_checkpoints</c> (#2459, ADR-0060).
/// </summary>
/// <remarks>
/// Replaces the dev/test-only <see cref="FileSystemMigrationRunCheckpointStore"/> as the
/// production persistence path so resumable migration-run state lives in the shared data
/// plane rather than on a compute node's local disk; a run checkpointed on one node can
/// resume on any node. The <see cref="MigrationRunCheckpointSanitizer"/> is applied on
/// write so persisted markers stay release-safe, and the checkpoint JSON round-trips
/// through the shared source-generated <see cref="MigrationRunCheckpointJsonContext"/> so
/// persistence stays AOT-safe and reflection-free.
/// </remarks>
internal sealed class PostgresMigrationRunCheckpointStore : IMigrationRunCheckpointStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;

    public PostgresMigrationRunCheckpointStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(MigrationRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var sanitized = MigrationRunCheckpointSanitizer.Sanitize(checkpoint);

        const string sql = """
            INSERT INTO honua.migration_run_checkpoints (run_id, checkpoint, updated_at)
            VALUES (@run_id, @checkpoint::jsonb, now())
            ON CONFLICT (run_id) DO UPDATE SET
                checkpoint = EXCLUDED.checkpoint,
                updated_at = now()
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, sanitized.RunId);
        command.Parameters.AddWithValue(
            "checkpoint",
            NpgsqlDbType.Text,
            JsonSerializer.Serialize(sanitized, MigrationRunCheckpointJsonContext.Default.MigrationRunCheckpoint));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MigrationRunCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        const string sql = """
            SELECT checkpoint
            FROM honua.migration_run_checkpoints
            WHERE run_id = @run_id
            LIMIT 1
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(0);
        var checkpoint = JsonSerializer.Deserialize(json, MigrationRunCheckpointJsonContext.Default.MigrationRunCheckpoint);
        return checkpoint is null ? null : MigrationRunCheckpointSanitizer.Sanitize(checkpoint);
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        const string sql = """
            DELETE FROM honua.migration_run_checkpoints
            WHERE run_id = @run_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, runId);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }
}
