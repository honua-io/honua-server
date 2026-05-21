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
    private const string TruncationMarker = "…";

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAuditLog> _logger;
    private readonly string _table;

    public PostgresAuditLog(
        IDatabaseConnectionProvider connectionProvider,
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

        var sql = $"""
            INSERT INTO {_table} (
                timestamp, event_type, actor, actor_type,
                resource_type, resource_id, action, outcome,
                correlation_id, remote_ip, user_agent, details
            ) VALUES (
                @timestamp, @event_type, @actor, @actor_type,
                @resource_type, @resource_id, @action, @outcome,
                @correlation_id, @remote_ip, @user_agent, @details
            )
            """;

        try
        {
            await using var connection = await _connectionProvider
                .OpenNpgsqlConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@timestamp", auditEvent.Timestamp.UtcDateTime);
            command.Parameters.AddWithValue("@event_type", auditEvent.EventType.ToString());
            command.Parameters.AddWithValue("@actor", Truncate(auditEvent.Actor, MaxActorLength));
            command.Parameters.AddWithValue("@actor_type", auditEvent.ActorType.ToString());
            command.Parameters.AddWithValue("@resource_type", Truncate(auditEvent.ResourceType, MaxResourceTypeLength));
            command.Parameters.AddWithValue("@resource_id",
                auditEvent.ResourceId is null
                    ? (object)DBNull.Value
                    : Truncate(auditEvent.ResourceId, MaxResourceIdLength));
            command.Parameters.AddWithValue("@action", Truncate(auditEvent.Action, MaxActionLength));
            command.Parameters.AddWithValue("@outcome", auditEvent.Outcome.ToString());
            command.Parameters.AddWithValue("@correlation_id", Truncate(auditEvent.CorrelationId, MaxCorrelationIdLength));
            command.Parameters.AddWithValue("@remote_ip",
                auditEvent.RemoteIp is null
                    ? (object)DBNull.Value
                    : Truncate(auditEvent.RemoteIp, MaxRemoteIpLength));
            command.Parameters.AddWithValue("@user_agent",
                auditEvent.UserAgent is null
                    ? (object)DBNull.Value
                    : Truncate(auditEvent.UserAgent, MaxUserAgentLength));
            command.Parameters.AddWithValue("@details", auditEvent.Details ?? string.Empty);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static string Truncate(string value, int max)
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
