// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

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
    private const string UpsertStateSql = """
        WITH payload(rule_id, layer_id, objectid, inside, entered_at, last_alert_at, last_generation, threshold_state) AS (
            SELECT *
            FROM unnest(@rule_ids, @layer_ids, @object_ids, @inside, @entered_at, @last_alert_at, @last_generation, @threshold_state)
        )
        INSERT INTO honua.alert_state (
            rule_id, layer_id, objectid, inside, entered_at, last_evaluated_at,
            last_alert_at, last_generation, threshold_state)
        SELECT
            payload.rule_id,
            payload.layer_id,
            payload.objectid,
            payload.inside,
            payload.entered_at,
            now(),
            payload.last_alert_at,
            payload.last_generation,
            payload.threshold_state::jsonb
        FROM payload
        ON CONFLICT (rule_id, layer_id, objectid)
        DO UPDATE SET
            inside = EXCLUDED.inside,
            entered_at = EXCLUDED.entered_at,
            last_evaluated_at = now(),
            last_alert_at = EXCLUDED.last_alert_at,
            last_generation = EXCLUDED.last_generation,
            threshold_state = EXCLUDED.threshold_state
        """;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly Action<AlertEvaluationCommitBoundary>? _faultInjector;

    public PostgresAlertOutboxWriter(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        Action<AlertEvaluationCommitBoundary>? faultInjector = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _faultInjector = faultInjector;
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

    public async Task<ImmutableArray<bool>> CommitEvaluationAsync(
        IReadOnlyCollection<AlertStateSnapshot> states,
        IReadOnlyList<AlertOutboxEntry> dispatches,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(dispatches);

        await using var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var inserted = ImmutableArray.CreateBuilder<bool>(dispatches.Count);

        if (states.Count > 0)
        {
            var normalizedStates = states
                .GroupBy(static state => new AlertStateLookupKey(state.RuleId, state.LayerId, state.ObjectId))
                .Select(static group => group.Last())
                .ToArray();

            await using var stateCommand = new NpgsqlCommand(UpsertStateSql, connection, transaction);
            stateCommand.Parameters.AddWithValue("rule_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, normalizedStates.Select(static state => state.RuleId).ToArray());
            stateCommand.Parameters.AddWithValue("layer_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, normalizedStates.Select(static state => state.LayerId).ToArray());
            stateCommand.Parameters.AddWithValue("object_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, normalizedStates.Select(static state => state.ObjectId).ToArray());
            stateCommand.Parameters.AddWithValue("inside", NpgsqlDbType.Array | NpgsqlDbType.Boolean, normalizedStates.Select(static state => state.Inside).ToArray());
            stateCommand.Parameters.AddWithValue("entered_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, normalizedStates.Select(static state => state.EnteredAt).ToArray());
            stateCommand.Parameters.AddWithValue("last_alert_at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, normalizedStates.Select(static state => state.LastAlertAt).ToArray());
            stateCommand.Parameters.AddWithValue("last_generation", NpgsqlDbType.Array | NpgsqlDbType.Bigint, normalizedStates.Select(static state => state.LastGeneration).ToArray());
            stateCommand.Parameters.AddWithValue("threshold_state", NpgsqlDbType.Array | NpgsqlDbType.Text, normalizedStates.Select(static state => state.ThresholdStateJson).ToArray());
            _ = await stateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        _faultInjector?.Invoke(AlertEvaluationCommitBoundary.StateWritten);

        foreach (var dispatch in dispatches)
        {
            long? eventId;
            await using (var appendCommand = new NpgsqlCommand(AlertOutboxCommands.AppendEventSql, connection, transaction))
            {
                AlertOutboxCommands.BindAppendEvent(appendCommand, dispatch.AlertEvent);
                eventId = await appendCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
            }

            if (!eventId.HasValue)
            {
                inserted.Add(false);
                continue;
            }

            inserted.Add(true);
            _faultInjector?.Invoke(AlertEvaluationCommitBoundary.EventWritten);

            var channelTypes = AlertOutboxCommands.ChannelDbValues(dispatch.Channels);
            if (channelTypes.Length == 0)
            {
                continue;
            }

            await using var enqueueCommand = new NpgsqlCommand(AlertOutboxCommands.EnqueueDispatchSql, connection, transaction);
            AlertOutboxCommands.BindEnqueue(enqueueCommand, eventId.Value, channelTypes);
            _ = await enqueueCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector?.Invoke(AlertEvaluationCommitBoundary.DispatchWritten);
        }

        _faultInjector?.Invoke(AlertEvaluationCommitBoundary.BeforeCommit);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return inserted.MoveToImmutable();
    }
}

internal enum AlertEvaluationCommitBoundary
{
    StateWritten,
    EventWritten,
    DispatchWritten,
    BeforeCommit
}
