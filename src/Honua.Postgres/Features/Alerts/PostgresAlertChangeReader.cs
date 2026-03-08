// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Alerts;

internal sealed class PostgresAlertChangeReader : IAlertChangeReader
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertChangeReader(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IReadOnlyList<AlertChange>> GetChangesAfterAsync(
        long lastGeneration,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT generation, layer_id, objectid, operation, changed_at
            FROM honua.feature_changes
            WHERE generation > @last_generation
            ORDER BY generation
            LIMIT @max_count
            """;

        await using var connection = (NpgsqlConnection)await _connectionProvider
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("last_generation", NpgsqlDbType.Bigint, lastGeneration);
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);

        var changes = new List<AlertChange>(Math.Max(1, maxCount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new AlertChange
            {
                Generation = reader.GetInt64(0),
                LayerId = reader.GetInt32(1),
                ObjectId = reader.GetInt64(2),
                Operation = AlertStoreConversions.ToChangeOperation(reader.GetInt16(3)),
                ChangedAt = reader.GetFieldValue<DateTimeOffset>(4)
            });
        }

        return changes;
    }
}
