// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

/// <summary>
/// PostgreSQL store for the operator lifecycle row attached to an alert event.
/// </summary>
/// <remarks>
/// <see cref="ApplyAsync"/> is the operator-action path: it writes the lifecycle
/// mutation and its domain audit intent in ONE transaction so a committed
/// mutation is never externally observable without either its audit record or a
/// durable reconciliation record that completes it (#3865). The individual
/// acknowledge/suppress/resolve methods remain for seeding and internal callers
/// that are not operator actions.
/// </remarks>
internal sealed class PostgresAlertLifecycleStore : IAlertLifecycleStore
{
    private const string LifecycleProjection =
        "lifecycle_status, acknowledged_at, acknowledged_by, " +
        "suppressed_until, suppressed_by, resolved_at, resolved_by, note, updated_at";

    private const string OutboxProjection =
        "outbox_id, event_id, action, actor, note, details, correlation_id, " +
        "idempotency_key, occurred_at, audit_id, completed_at";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _lifecycleTable;
    private readonly string _eventsTable;
    private readonly string _outboxTable;

    public PostgresAlertLifecycleStore(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _lifecycleTable = SchemaSearchPath.QualifyTable("alert_event_lifecycle", schemaName);
        _eventsTable = SchemaSearchPath.QualifyTable("alert_events", schemaName);
        _outboxTable = SchemaSearchPath.QualifyTable("alert_audit_outbox", schemaName);
    }

    public async Task<AlertEventLifecycle?> GetAsync(long eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLifecycleAsync(connection, transaction: null, eventId, cancellationToken).ConfigureAwait(false);
    }

    public Task<AlertEventLifecycle?> AcknowledgeAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default)
        => UpsertAsync(
            AcknowledgeSql(),
            eventId,
            command => BindAcknowledge(command, actor, note, acknowledgedAt),
            cancellationToken);

    public Task<AlertEventLifecycle?> SuppressAsync(
        long eventId,
        string actor,
        DateTimeOffset suppressUntil,
        string? note,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken = default)
        => UpsertAsync(
            SuppressSql(),
            eventId,
            command => BindSuppress(command, actor, suppressUntil, note, suppressedAt),
            cancellationToken);

    public Task<AlertEventLifecycle?> ResolveAsync(
        long eventId,
        string actor,
        string? note,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
        => UpsertAsync(
            ResolveSql(),
            eventId,
            command => BindResolve(command, actor, note, resolvedAt),
            cancellationToken);

    /// <inheritdoc />
    public async Task<AlertLifecycleTransition> ApplyAsync(
        AlertLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Action == AlertLifecycleAction.Suppress && command.SuppressUntil is null)
        {
            throw new ArgumentException("A suppress command requires 'SuppressUntil'.", nameof(command));
        }

        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A replay of the same operator action must produce ONE logical transition
        // and ONE domain audit action, so an already-recorded intent short-circuits
        // the mutation rather than re-applying it.
        var existing = await ReadIntentByKeyAsync(connection, transaction, command.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var replayed = await ReadLifecycleAsync(connection, transaction, command.EventId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AlertLifecycleTransition { Lifecycle = replayed, Intent = existing, Replayed = true };
        }

        var lifecycle = await MutateAsync(connection, transaction, command, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            // No such event: nothing was mutated, so no audit intent may exist either.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new AlertLifecycleTransition { Lifecycle = null };
        }

        AlertAuditOutboxEntry intent;
        try
        {
            intent = await InsertIntentAsync(connection, transaction, command, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A concurrent request carrying the same idempotency identity won the
            // race. Abandon this mutation and replay theirs.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await ReplayWinnerAsync(command, cancellationToken).ConfigureAwait(false);
        }

        // Both writes commit together: the mutation is never externally observable
        // without its durable audit intent.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AlertLifecycleTransition { Lifecycle = lifecycle, Intent = intent };
    }

    // -------------------------------------------------------------------------
    // Statements
    // -------------------------------------------------------------------------

    private string AcknowledgeSql() => $"""
        INSERT INTO {_lifecycleTable} (
            event_id, lifecycle_status, acknowledged_at, acknowledged_by, note, updated_at)
        SELECT @event_id, @status, @acknowledged_at, @acknowledged_by, @note, @acknowledged_at
        WHERE EXISTS (SELECT 1 FROM {_eventsTable} WHERE event_id = @event_id)
        ON CONFLICT (event_id) DO UPDATE SET
            lifecycle_status = EXCLUDED.lifecycle_status,
            acknowledged_at  = EXCLUDED.acknowledged_at,
            acknowledged_by  = EXCLUDED.acknowledged_by,
            suppressed_until = NULL,
            suppressed_by    = NULL,
            resolved_at      = NULL,
            resolved_by      = NULL,
            note             = EXCLUDED.note,
            updated_at       = EXCLUDED.updated_at
        RETURNING {LifecycleProjection}
        """;

    private string SuppressSql() => $"""
        INSERT INTO {_lifecycleTable} (
            event_id, lifecycle_status, suppressed_until, suppressed_by, note, updated_at)
        SELECT @event_id, @status, @suppressed_until, @suppressed_by, @note, @updated_at
        WHERE EXISTS (SELECT 1 FROM {_eventsTable} WHERE event_id = @event_id)
        ON CONFLICT (event_id) DO UPDATE SET
            lifecycle_status = EXCLUDED.lifecycle_status,
            acknowledged_at  = NULL,
            acknowledged_by  = NULL,
            suppressed_until = EXCLUDED.suppressed_until,
            suppressed_by    = EXCLUDED.suppressed_by,
            resolved_at      = NULL,
            resolved_by      = NULL,
            note             = EXCLUDED.note,
            updated_at       = EXCLUDED.updated_at
        RETURNING {LifecycleProjection}
        """;

    private string ResolveSql() => $"""
        INSERT INTO {_lifecycleTable} (
            event_id, lifecycle_status, resolved_at, resolved_by, note, updated_at)
        SELECT @event_id, @status, @resolved_at, @resolved_by, @note, @resolved_at
        WHERE EXISTS (SELECT 1 FROM {_eventsTable} WHERE event_id = @event_id)
        ON CONFLICT (event_id) DO UPDATE SET
            lifecycle_status = EXCLUDED.lifecycle_status,
            acknowledged_at  = NULL,
            acknowledged_by  = NULL,
            suppressed_until = NULL,
            suppressed_by    = NULL,
            resolved_at      = EXCLUDED.resolved_at,
            resolved_by      = EXCLUDED.resolved_by,
            note             = EXCLUDED.note,
            updated_at       = EXCLUDED.updated_at
        RETURNING {LifecycleProjection}
        """;

    private static void BindAcknowledge(NpgsqlCommand command, string actor, string? note, DateTimeOffset at)
    {
        command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint,
            AlertLifecycleConversions.ToDbValue(AlertLifecycleStatus.Acknowledged));
        command.Parameters.AddWithValue("acknowledged_at", NpgsqlDbType.TimestampTz, at);
        command.Parameters.AddWithValue("acknowledged_by", NpgsqlDbType.Text, actor);
        command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
    }

    private static void BindSuppress(
        NpgsqlCommand command, string actor, DateTimeOffset suppressUntil, string? note, DateTimeOffset at)
    {
        command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint,
            AlertLifecycleConversions.ToDbValue(AlertLifecycleStatus.Suppressed));
        command.Parameters.AddWithValue("suppressed_until", NpgsqlDbType.TimestampTz, suppressUntil);
        command.Parameters.AddWithValue("suppressed_by", NpgsqlDbType.Text, actor);
        command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, at);
    }

    private static void BindResolve(NpgsqlCommand command, string actor, string? note, DateTimeOffset at)
    {
        command.Parameters.AddWithValue("status", NpgsqlDbType.Smallint,
            AlertLifecycleConversions.ToDbValue(AlertLifecycleStatus.Resolved));
        command.Parameters.AddWithValue("resolved_at", NpgsqlDbType.TimestampTz, at);
        command.Parameters.AddWithValue("resolved_by", NpgsqlDbType.Text, actor);
        command.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
    }

    // -------------------------------------------------------------------------
    // Execution
    // -------------------------------------------------------------------------

    private async Task<AlertEventLifecycle?> UpsertAsync(
        string sql,
        long eventId,
        Action<NpgsqlCommand> bindParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);
        bindParameters(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLifecycle(reader, eventId)
            : null;
    }

    private async Task<AlertEventLifecycle?> MutateAsync(
        NpgsqlConnectionLease connection,
        NpgsqlTransaction transaction,
        AlertLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        string sql;
        Action<NpgsqlCommand> bind;
        switch (command.Action)
        {
            case AlertLifecycleAction.Acknowledge:
                sql = AcknowledgeSql();
                bind = dbCommand => BindAcknowledge(dbCommand, command.Actor, command.Note, command.OccurredAt);
                break;
            case AlertLifecycleAction.Suppress:
                sql = SuppressSql();
                bind = dbCommand => BindSuppress(
                    dbCommand, command.Actor, command.SuppressUntil!.Value, command.Note, command.OccurredAt);
                break;
            case AlertLifecycleAction.Resolve:
                sql = ResolveSql();
                bind = dbCommand => BindResolve(dbCommand, command.Actor, command.Note, command.OccurredAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command), command.Action, "Unknown alert lifecycle action.");
        }

        await using var mutation = new NpgsqlCommand(sql, connection, transaction);
        mutation.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, command.EventId);
        bind(mutation);

        await using var reader = await mutation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLifecycle(reader, command.EventId)
            : null;
    }

    private async Task<AlertAuditOutboxEntry> InsertIntentAsync(
        NpgsqlConnectionLease connection,
        NpgsqlTransaction transaction,
        AlertLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_outboxTable} (
                event_id, action, actor, note, details, correlation_id, idempotency_key, occurred_at)
            VALUES (@event_id, @action, @actor, @note, @details, @correlation_id, @idempotency_key, @occurred_at)
            RETURNING outbox_id
            """;

        await using var insert = new NpgsqlCommand(sql, connection, transaction);
        insert.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, command.EventId);
        insert.Parameters.AddWithValue("action", NpgsqlDbType.Text, command.AuditAction);
        insert.Parameters.AddWithValue("actor", NpgsqlDbType.Text, command.Actor);
        insert.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)command.Note ?? DBNull.Value);
        insert.Parameters.AddWithValue("details", NpgsqlDbType.Text, command.Details);
        insert.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Text, command.CorrelationId);
        insert.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, command.IdempotencyKey);
        insert.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, command.OccurredAt);

        var outboxId = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new AlertAuditOutboxEntry
        {
            OutboxId = outboxId,
            EventId = command.EventId,
            Action = command.AuditAction,
            Actor = command.Actor,
            Note = command.Note,
            Details = command.Details,
            CorrelationId = command.CorrelationId,
            IdempotencyKey = command.IdempotencyKey,
            OccurredAt = command.OccurredAt,
        };
    }

    private async Task<AlertLifecycleTransition> ReplayWinnerAsync(
        AlertLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var winner = await ReadIntentByKeyAsync(connection, transaction: null, command.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var lifecycle = await ReadLifecycleAsync(connection, transaction: null, command.EventId, cancellationToken)
            .ConfigureAwait(false);
        return new AlertLifecycleTransition { Lifecycle = lifecycle, Intent = winner, Replayed = true };
    }

    private async Task<AlertAuditOutboxEntry?> ReadIntentByKeyAsync(
        NpgsqlConnectionLease connection,
        NpgsqlTransaction? transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {OutboxProjection} FROM {_outboxTable} WHERE idempotency_key = @idempotency_key";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? AlertAuditOutboxMapper.Read(reader)
            : null;
    }

    private async Task<AlertEventLifecycle?> ReadLifecycleAsync(
        NpgsqlConnectionLease connection,
        NpgsqlTransaction? transaction,
        long eventId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {LifecycleProjection} FROM {_lifecycleTable} WHERE event_id = @event_id";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Bigint, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLifecycle(reader, eventId)
            : null;
    }

    private static AlertEventLifecycle ReadLifecycle(NpgsqlDataReader reader, long eventId) => new()
    {
        EventId = eventId,
        Status = AlertLifecycleConversions.ToLifecycleStatus(reader.GetInt16(0)),
        AcknowledgedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
        AcknowledgedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
        SuppressedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
        SuppressedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
        ResolvedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        ResolvedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
        Note = reader.IsDBNull(7) ? null : reader.GetString(7),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8)
    };
}
