// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditLogIntegrityVerifier"/> (#350).
/// Replays the tamper-evident hash chain stored by <see cref="PostgresAuditLog"/>
/// and reports the first divergence.
/// </summary>
internal sealed class PostgresAuditLogIntegrityVerifier : IAuditLogIntegrityVerifier
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;

    public PostgresAuditLogIntegrityVerifier(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
    }

    public async Task<AuditIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT audit_id, timestamp, event_type, actor, actor_type, resource_type, resource_id,
                   action, outcome, correlation_id, remote_ip, user_agent, details, prev_hash, entry_hash
            FROM {_table}
            ORDER BY audit_id ASC
            """;

        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        long rowsChecked = 0;
        long unhashedRows = 0;
        string? expectedPrevHash = null;
        var chainStarted = false;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rowsChecked++;

            var auditId = reader.GetInt64(0);
            var storedPrevHash = reader.IsDBNull(13) ? null : reader.GetString(13);
            var storedEntryHash = reader.IsDBNull(14) ? null : reader.GetString(14);

            if (storedEntryHash is null)
            {
                // Row predates migration 069 — counted but not part of the chain.
                unhashedRows++;
                continue;
            }

            // The prev_hash of each hashed row must link to the previous hashed
            // row's entry_hash (or be NULL for the genesis hashed row). A break
            // here means a row was deleted or reordered.
            if (!chainStarted)
            {
                chainStarted = true;
            }
            else if (!string.Equals(storedPrevHash, expectedPrevHash, StringComparison.Ordinal))
            {
                return Broken(rowsChecked, unhashedRows, auditId,
                    $"prev_hash mismatch at audit_id {auditId}: chain link broken (row deleted or reordered).");
            }

            var timestamp = reader.GetFieldValue<DateTimeOffset>(1);
            var eventType = ParseEnum(reader.GetString(2), AuditEventType.AdminAction);
            var actor = reader.GetString(3);
            var actorType = ParseEnum(reader.GetString(4), AuditActorType.Anonymous);
            var resourceType = reader.GetString(5);
            var resourceId = reader.IsDBNull(6) ? null : reader.GetString(6);
            var action = reader.GetString(7);
            var outcome = ParseEnum(reader.GetString(8), AuditOutcome.Failure);
            var correlationId = reader.GetString(9);
            var remoteIp = reader.IsDBNull(10) ? null : reader.GetString(10);
            var userAgent = reader.IsDBNull(11) ? null : reader.GetString(11);
            var details = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);

            var recomputed = AuditEntryHasher.ComputeEntryHash(
                storedPrevHash,
                timestamp,
                eventType,
                actor,
                actorType,
                resourceType,
                resourceId,
                action,
                outcome,
                correlationId,
                remoteIp,
                userAgent,
                details);

            if (!string.Equals(recomputed, storedEntryHash, StringComparison.Ordinal))
            {
                return Broken(rowsChecked, unhashedRows, auditId,
                    $"entry_hash mismatch at audit_id {auditId}: row contents were altered after write.");
            }

            expectedPrevHash = storedEntryHash;
        }

        return new AuditIntegrityReport
        {
            Verified = true,
            RowsChecked = rowsChecked,
            UnhashedRows = unhashedRows,
        };
    }

    private static AuditIntegrityReport Broken(long rowsChecked, long unhashedRows, long auditId, string reason)
        => new()
        {
            Verified = false,
            RowsChecked = rowsChecked,
            UnhashedRows = unhashedRows,
            FirstBrokenAuditId = auditId,
            FailureReason = reason,
        };

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ? parsed : fallback;
}
