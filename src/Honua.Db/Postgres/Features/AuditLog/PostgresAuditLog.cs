// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.AuditLog;

internal sealed class PostgresAuditLog : IAuditLog
{
    // Max field widths must agree with migration 033. Values longer than the
    // column limit are truncated here rather than dropped, so a misbehaving
    // caller still produces a row (with a marker) instead of silently losing
    // an audit event to a constraint violation.
    private const int MaxActorLength = 256;
    private const int MaxResourceTypeLength = 64;
    private const int MaxResourceIdLength = 256;
    private const int MaxActionLength = 128;
    private const int MaxCorrelationIdLength = 64;
    private const int MaxRemoteIpLength = 64;
    private const int MaxUserAgentLength = 512;
    // Horizontal ellipsis (U+2026). Written as a Unicode escape so the source
    // stays ASCII-clean and cannot be silently re-corrupted into mojibake
    // (the UTF-8 bytes read back as Latin-1) by a tool that mis-round-trips it.
    private const string TruncationMarker = "\u2026";

    // Stable, table-scoped key for the transaction advisory lock that serializes
    // hash-chain construction. Concurrent audit inserts briefly contend on
    // this lock so the (prev_hash -> entry_hash) chain stays linear; audit volume
    // is low (security-relevant events only) and the busy MCP access writer
    // batches its inserts under one lock, so the contention is negligible.
    private const long HashChainAdvisoryLockKey = 0x484F4E55_41554449L; // "HONUAUDI"

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAuditLog> _logger;
    private readonly string _table;
    private readonly byte[] _chainKey;

    public PostgresAuditLog(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAuditLog> logger,
        string? schemaName = null,
        ReadOnlyMemory<byte> chainKey = default)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionProvider = connectionProvider;
        _logger = logger;
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
        _chainKey = chainKey.ToArray();
    }

    public async Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        try
        {
            var auditIds = await WriteChainedAsync([auditEvent], cancellationToken).ConfigureAwait(false);
            return auditIds[0];
        }
        catch (OperationCanceledException)
        {
            // Honour cooperative cancellation; do not log, the caller is shutting down.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: audit-write failures must NOT block the audited action; we log
            // and continue. (The IAuditLog contract documents this best-effort policy.)
            AuditLogPostgresLog.RecordFailed(_logger, auditEvent.Action, ex);
            return null;
        }
    }

    // One transaction and one chain lock for the whole batch, so a busy writer such as the MCP
    // access audit queue takes the lock once per batch instead of once per event. If the batch
    // fails, each event is retried on its own so one bad row does not drop the others.
    public async Task RecordBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);
        if (auditEvents.Count == 0)
        {
            return;
        }

        try
        {
            await WriteChainedAsync(auditEvents, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException ex)
        {
            // The server rejected a statement, so one row may be at fault: retry each event on
            // its own. Inside a borrowed transaction the rejection aborts the caller's
            // transaction too, and the retries fail with it; the queue never borrows one.
            AuditLogPostgresLog.BatchRecordFailed(_logger, auditEvents.Count, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Connection or transport failure: retrying event by event would wait out the
            // connect timeout once per event and log each, so the batch is reported once.
            AuditLogPostgresLog.BatchDropped(_logger, auditEvents.Count, ex);
            return;
        }

        foreach (var auditEvent in auditEvents)
        {
            await RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?[]> WriteChainedAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken)
    {
        // Compute the stored (truncated) field values up front so the hash chain
        // is built over exactly what is persisted; the verifier replays the same
        // stored values, so the two hashes must agree.
        var entries = new AuditEntryHashInput[auditEvents.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = StoredEntry(auditEvents[i]);
        }

        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Borrow the actual explicit mutation transaction, regardless of the
        // connection string's auto-enlistment setting. Otherwise own a local one.
        await using var ownedTransaction = connection.Transaction is null
            ? await connection.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var transaction = connection.Transaction ?? ownedTransaction;

        var auditIds = await AppendAsync(new ChainWriter(connection, transaction), entries, cancellationToken)
            .ConfigureAwait(false);

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        }

        return auditIds;
    }

    // Details are not length-capped, but NUL and unpaired surrogates still have to
    // be removed before the hash is computed. Postgres rejects NUL, and Npgsql's
    // UTF-8 encoder throws on a lone surrogate, which drops the whole row.
    private static AuditEntryHashInput StoredEntry(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return new AuditEntryHashInput
        {
            Timestamp = auditEvent.Timestamp,
            EventType = auditEvent.EventType,
            Actor = Truncate(auditEvent.Actor, MaxActorLength),
            ActorType = auditEvent.ActorType,
            ResourceType = Truncate(auditEvent.ResourceType, MaxResourceTypeLength),
            ResourceId = auditEvent.ResourceId is null ? null : Truncate(auditEvent.ResourceId, MaxResourceIdLength),
            Action = Truncate(auditEvent.Action, MaxActionLength),
            Outcome = auditEvent.Outcome,
            CorrelationId = Truncate(auditEvent.CorrelationId, MaxCorrelationIdLength),
            RemoteIp = auditEvent.RemoteIp is null ? null : Truncate(auditEvent.RemoteIp, MaxRemoteIpLength),
            UserAgent = auditEvent.UserAgent is null ? null : Truncate(auditEvent.UserAgent, MaxUserAgentLength),
            Details = SanitizeText(auditEvent.Details),
            ActorAgent = auditEvent.ActorAgent is null ? null : Truncate(auditEvent.ActorAgent, MaxActorLength),
        };
    }

    // Serialize chain construction so the latest entry_hash we read is the true tail of the
    // chain, then link each entry to the one before it. The advisory lock is transaction-scoped
    // and released on commit/rollback.
    private async Task<string?[]> AppendAsync(
        ChainWriter writer, AuditEntryHashInput[] entries, CancellationToken cancellationToken)
    {
        await using (var lockCommand = writer.Command("SELECT pg_advisory_xact_lock(@key)"))
        {
            lockCommand.Parameters.AddWithValue("@key", HashChainAdvisoryLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var previousHash = await ReadChainTailAsync(writer, cancellationToken).ConfigureAwait(false);
        var auditIds = new string?[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            (auditIds[i], previousHash) = await InsertAsync(
                writer, entries[i] with { PreviousHash = previousHash }, cancellationToken).ConfigureAwait(false);
        }

        return auditIds;
    }

    private async Task<string?> ReadChainTailAsync(ChainWriter writer, CancellationToken cancellationToken)
    {
        await using var tailCommand = writer.Command(
            $"SELECT entry_hash FROM {_table} WHERE entry_hash IS NOT NULL ORDER BY audit_id DESC LIMIT 1");
        var tail = await tailCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var previousHash = tail is null or DBNull ? null : ((string)tail).Trim();
        return previousHash is { Length: 0 } ? null : previousHash;
    }

    // Inserts one linked entry and returns its id and the hash the next entry links to.
    private async Task<(string? AuditId, string EntryHash)> InsertAsync(
        ChainWriter writer, AuditEntryHashInput entry, CancellationToken cancellationToken)
    {
        var entryHash = _chainKey.Length >= AuditChainKeyMaterial.MinimumLength
            ? AuditEntryHasher.ComputeEntryMac(_chainKey, entry)
            : AuditEntryHasher.ComputeEntryHash(entry);
        var sql = $"""
            INSERT INTO {_table} (
                timestamp, event_type, actor, actor_type,
                resource_type, resource_id, action, outcome,
                correlation_id, remote_ip, user_agent, details,
                prev_hash, entry_hash, actor_agent
            ) VALUES (
                @timestamp, @event_type, @actor, @actor_type,
                @resource_type, @resource_id, @action, @outcome,
                @correlation_id, @remote_ip, @user_agent, @details,
                @prev_hash, @entry_hash, @actor_agent
            )
            RETURNING audit_id
            """;

        await using var command = writer.Command(sql);
        command.Parameters.AddWithValue("@timestamp", entry.Timestamp.UtcDateTime);
        command.Parameters.AddWithValue("@event_type", entry.EventType.ToString());
        command.Parameters.AddWithValue("@actor", entry.Actor);
        command.Parameters.AddWithValue("@actor_agent", NpgsqlTypes.NpgsqlDbType.Varchar, (object?)entry.ActorAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("@actor_type", entry.ActorType.ToString());
        command.Parameters.AddWithValue("@resource_type", entry.ResourceType);
        command.Parameters.AddWithValue("@resource_id", (object?)entry.ResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@action", entry.Action);
        command.Parameters.AddWithValue("@outcome", entry.Outcome.ToString());
        command.Parameters.AddWithValue("@correlation_id", entry.CorrelationId);
        command.Parameters.AddWithValue("@remote_ip", (object?)entry.RemoteIp ?? DBNull.Value);
        command.Parameters.AddWithValue("@user_agent", (object?)entry.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("@details", entry.Details);
        command.Parameters.AddWithValue("@prev_hash", (object?)entry.PreviousHash ?? DBNull.Value);
        command.Parameters.AddWithValue("@entry_hash", entryHash);

        var assignedAuditId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (Convert.ToString(assignedAuditId, CultureInfo.InvariantCulture), entryHash);
    }

    // The connection and transaction one chain append runs on.
    private readonly record struct ChainWriter(NpgsqlConnection Connection, NpgsqlTransaction? Transaction)
    {
        public NpgsqlCommand Command(string sql) => new(sql, Connection, Transaction);
    }

    // Scrubs text that PostgreSQL or the UTF-8 encoder cannot store, then truncates
    // by UTF-16 char count (not bytes), appending the ellipsis marker so the
    // truncation is visible. Exposed to the Postgres test assembly for direct,
    // Docker-free coverage of the marker/encoding contract. RecordAsync hashes
    // these same values, so the chain matches the row.
    internal static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        value = SanitizeText(value);
        if (value.Length <= max)
        {
            return value;
        }

        // Keep room for a single-character marker so the truncation is visible
        // to forensic readers without breaking the column width contract.
        var contentLength = max - TruncationMarker.Length;
        if (contentLength > 0 && char.IsHighSurrogate(value[contentLength - 1]))
        {
            contentLength--;
        }

        return string.Concat(value.AsSpan(0, contentLength), TruncationMarker);
    }

    internal static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value) || !NeedsSanitize(value))
        {
            return value ?? string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current == '\0')
            {
                continue;
            }

            if (char.IsHighSurrogate(current))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(current);
                    builder.Append(value[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(current))
            {
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool NeedsSanitize(string value)
    {
        foreach (var current in value)
        {
            if (current == '\0' || char.IsSurrogate(current))
            {
                return true;
            }
        }

        return false;
    }
}

internal static partial class AuditLogPostgresLog
{
    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Error,
        Message = "Failed to persist audit event for action '{Action}'. Event has been dropped.")]
    public static partial void RecordFailed(ILogger logger, string action, Exception exception);

    [LoggerMessage(
        EventId = 7304,
        Level = LogLevel.Warning,
        Message = "Failed to persist a batch of {Count} audit events together; retrying each event on its own.")]
    public static partial void BatchRecordFailed(ILogger logger, int count, Exception exception);

    [LoggerMessage(
        EventId = 7305,
        Level = LogLevel.Error,
        Message = "Failed to reach the audit store for a batch of {Count} audit events. The batch has been dropped.")]
    public static partial void BatchDropped(ILogger logger, int count, Exception exception);
}
