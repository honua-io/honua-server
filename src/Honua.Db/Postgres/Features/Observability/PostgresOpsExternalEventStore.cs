// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Observability;

internal sealed class PostgresOpsExternalEventStore : IOpsExternalEventStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connections;
    private readonly string _table;

    public PostgresOpsExternalEventStore(IAdoNetDatabaseConnectionProvider connections, string? schemaName = null)
    {
        _connections = connections;
        _table = SchemaSearchPath.QualifyTable("ops_external_events", schemaName);
    }

    public async Task<OpsExternalEvent> AppendAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_table} (event_id, occurred_at, body)
            VALUES (@id, @occurred_at, @body)
            ON CONFLICT (event_id) DO UPDATE SET event_id = EXCLUDED.event_id
            RETURNING event_id, occurred_at, body::text;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, externalEvent.Id);
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, externalEvent.OccurredAt);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, externalEvent.Body);
        OpsExternalEvent canonical;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            canonical = new OpsExternalEvent(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2));
        }

        command.CommandText = $"DELETE FROM {_table} WHERE received_at < now() - interval '30 days'";
        command.Parameters.Clear();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return canonical;
    }

    public async Task<IReadOnlyList<OpsExternalEvent>> ReadRecentAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT event_id, occurred_at, body::text FROM {_table}
            WHERE occurred_at >= @since ORDER BY occurred_at DESC, event_id LIMIT 1000
            """, connection);
        command.Parameters.AddWithValue("since", NpgsqlDbType.TimestampTz, since);
        var events = new List<OpsExternalEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new OpsExternalEvent(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2)));
        }

        return events;
    }
}
