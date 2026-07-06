// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.Alerts;

/// <summary>
/// Atomic writer that appends an alert event and enqueues its per-channel dispatch rows in a
/// SINGLE transaction on a SINGLE connection. This closes the alert-loss window that existed when
/// the event append and the dispatch enqueue ran on two separate, non-transactional connections:
/// a crash between the two committed the event but lost the dispatch, so the alert was persisted
/// yet never delivered — concentrated exactly when the host was unstable. Deriving the dispatch
/// rows from the just-committed event row inside one transaction makes the pair all-or-nothing.
/// </summary>
internal sealed class PostgresAlertOutboxWriter : IAlertOutboxWriter
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;

    public PostgresAlertOutboxWriter(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<long?> AppendAndEnqueueAsync(
        AlertEventEnvelope alertEvent,
        ImmutableArray<AlertChannelType> channels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long eventId;
        await using (var appendCommand = new NpgsqlCommand(AlertOutboxCommands.AppendEventSql, connection, transaction))
        {
            AlertOutboxCommands.BindAppendEvent(appendCommand, alertEvent);
            var result = await appendCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not long appended)
            {
                // Deduplicated: no event row was written, so there is nothing to enqueue.
                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            eventId = appended;
        }

        var channelTypes = AlertOutboxCommands.ChannelDbValues(channels);
        if (channelTypes.Length > 0)
        {
            await using var enqueueCommand = new NpgsqlCommand(AlertOutboxCommands.EnqueueDispatchSql, connection, transaction);
            AlertOutboxCommands.BindEnqueue(enqueueCommand, eventId, channelTypes);
            _ = await enqueueCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return eventId;
    }
}
