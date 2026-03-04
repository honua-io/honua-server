// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertCheckpointStore : IAlertCheckpointStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertCheckpointStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<AlertWorkerCheckpoint> GetAsync(
        string workerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);

        const string selectSql = """
            SELECT worker_name, last_generation, last_dwell_sweep_at
            FROM honua.alert_worker_checkpoint
            WHERE worker_name = @worker_name
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var select = new NpgsqlCommand(selectSql, connection);
        select.Parameters.AddWithValue("worker_name", NpgsqlDbType.Text, workerName);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AlertWorkerCheckpoint
            {
                WorkerName = reader.GetString(0),
                LastGeneration = reader.GetInt64(1),
                LastDwellSweepAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)
            };
        }

        await reader.DisposeAsync().ConfigureAwait(false);

        const string insertSql = """
            INSERT INTO honua.alert_worker_checkpoint (worker_name, last_generation)
            VALUES (@worker_name, 0)
            ON CONFLICT (worker_name) DO NOTHING
            """;

        await using var insert = new NpgsqlCommand(insertSql, connection);
        insert.Parameters.AddWithValue("worker_name", NpgsqlDbType.Text, workerName);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new AlertWorkerCheckpoint
        {
            WorkerName = workerName,
            LastGeneration = 0,
            LastDwellSweepAt = null
        };
    }

    public async Task SetAsync(AlertWorkerCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        const string sql = """
            INSERT INTO honua.alert_worker_checkpoint (worker_name, last_generation, last_dwell_sweep_at, updated_at)
            VALUES (@worker_name, @last_generation, @last_dwell_sweep_at, now())
            ON CONFLICT (worker_name)
            DO UPDATE SET
                last_generation = EXCLUDED.last_generation,
                last_dwell_sweep_at = EXCLUDED.last_dwell_sweep_at,
                updated_at = now()
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("worker_name", NpgsqlDbType.Text, checkpoint.WorkerName);
        command.Parameters.AddWithValue("last_generation", NpgsqlDbType.Bigint, checkpoint.LastGeneration);
        command.Parameters.AddWithValue("last_dwell_sweep_at", NpgsqlDbType.TimestampTz, (object?)checkpoint.LastDwellSweepAt ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
