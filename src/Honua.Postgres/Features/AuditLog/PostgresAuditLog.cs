// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL-backed <see cref="IAuditLog"/> writing to <c>honua.audit_log</c>.
/// Append-only by design (see migration 033) — only INSERTs are issued, and
/// transient failures are swallowed (logged) so that an audit-write hiccup
/// never blocks the security-relevant action being audited.
/// </summary>
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
    // hash-chain construction (#350). Concurrent audit inserts briefly contend on
    // this lock so the (prev_hash -> entry_hash) chain stays linear; audit volume
    // is low (security-relevant events only) so the contention is negligible.
    private const long HashChainAdvisoryLockKey = 0x484F4E55_41554449L; // "HONUAUDI"

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAuditLog> _logger;
    private readonly string _table;

    public PostgresAuditLog(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAuditLog> logger,
        string? schemaName = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionProvider = connectionProvider;
        _logger = logger;
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
    }

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Compute the stored (truncated) field values up front so the hash chain
        // is built over exactly what is persisted — the verifier replays the same
        // stored values, so the two hashes must agree.
        var actor = Truncate(auditEvent.Actor, MaxActorLength);
        var resourceType = Truncate(auditEvent.ResourceType, MaxResourceTypeLength);
        var resourceId = auditEvent.ResourceId is null ? null : Truncate(auditEvent.ResourceId, MaxResourceIdLength);
        var action = Truncate(auditEvent.Action, MaxActionLength);
        var correlationId = Truncate(auditEvent.CorrelationId, MaxCorrelationIdLength);
        var remoteIp = auditEvent.RemoteIp is null ? null : Truncate(auditEvent.RemoteIp, MaxRemoteIpLength);
        var userAgent = auditEvent.UserAgent is null ? null : Truncate(auditEvent.UserAgent, MaxUserAgentLength);
        var details = auditEvent.Details ?? string.Empty;

        var sql = $"""
            INSERT INTO {_table} (
                timestamp, event_type, actor, actor_type,
                resource_type, resource_id, action, outcome,
                correlation_id, remote_ip, user_agent, details,
                prev_hash, entry_hash
            ) VALUES (
                @timestamp, @event_type, @actor, @actor_type,
                @resource_type, @resource_id, @action, @outcome,
                @correlation_id, @remote_ip, @user_agent, @details,
                @prev_hash, @entry_hash
            )
            """;

        try
        {
            await using var connection = await _connectionProvider
                .OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Serialize chain construction so the latest entry_hash we read is the
            // true tail of the chain. The advisory lock is transaction-scoped and
            // released on commit/rollback.
            await using var transaction = await connection.Connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("@key", HashChainAdvisoryLockKey);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            string? previousHash;
            await using (var tailCommand = new NpgsqlCommand(
                $"SELECT entry_hash FROM {_table} ORDER BY audit_id DESC LIMIT 1", connection, transaction))
            {
                var tail = await tailCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                previousHash = tail is null or DBNull ? null : (string)tail;
            }

            var entryHash = AuditEntryHasher.ComputeEntryHash(
                previousHash,
                auditEvent.Timestamp,
                auditEvent.EventType,
                actor,
                auditEvent.ActorType,
                resourceType,
                resourceId,
                action,
                auditEvent.Outcome,
                correlationId,
                remoteIp,
                userAgent,
                details);

            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@timestamp", auditEvent.Timestamp.UtcDateTime);
                command.Parameters.AddWithValue("@event_type", auditEvent.EventType.ToString());
                command.Parameters.AddWithValue("@actor", actor);
                command.Parameters.AddWithValue("@actor_type", auditEvent.ActorType.ToString());
                command.Parameters.AddWithValue("@resource_type", resourceType);
                command.Parameters.AddWithValue("@resource_id", (object?)resourceId ?? DBNull.Value);
                command.Parameters.AddWithValue("@action", action);
                command.Parameters.AddWithValue("@outcome", auditEvent.Outcome.ToString());
                command.Parameters.AddWithValue("@correlation_id", correlationId);
                command.Parameters.AddWithValue("@remote_ip", (object?)remoteIp ?? DBNull.Value);
                command.Parameters.AddWithValue("@user_agent", (object?)userAgent ?? DBNull.Value);
                command.Parameters.AddWithValue("@details", details);
                command.Parameters.AddWithValue("@prev_hash", (object?)previousHash ?? DBNull.Value);
                command.Parameters.AddWithValue("@entry_hash", entryHash);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Honour cooperative cancellation — do not log; the caller is shutting down.
            throw;
        }
        catch (Exception ex)
        {
            // Audit-write failures must NOT block the audited action; we log and continue.
            // (The IAuditLog contract documents this best-effort policy.)
            AuditLogPostgresLog.RecordFailed(_logger, auditEvent.Action, ex);
        }
    }

    // Truncates by UTF-16 char count (not bytes), appending the ellipsis marker
    // so the truncation is visible. Exposed to the Postgres test assembly for
    // direct, Docker-free coverage of the marker/encoding contract.
    internal static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= max)
        {
            return value;
        }

        // Keep room for a single-character marker so the truncation is visible
        // to forensic readers without breaking the column width contract.
        return string.Concat(value.AsSpan(0, max - TruncationMarker.Length), TruncationMarker);
    }
}

internal static partial class AuditLogPostgresLog
{
    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Error,
        Message = "Failed to persist audit event for action '{Action}'. Event has been dropped.")]
    public static partial void RecordFailed(ILogger logger, string action, Exception exception);
}
