// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditLogIntegrityVerifier"/> (#350).
/// Replays the tamper-evident hash chain stored by <see cref="PostgresAuditLog"/>
/// and reports the first divergence.
/// </summary>
internal sealed class PostgresAuditLogIntegrityVerifier : IAuditLogIntegrityVerifier
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _table;
    private readonly byte[] _chainKey;

    public PostgresAuditLogIntegrityVerifier(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        string? schemaName = null,
        ReadOnlyMemory<byte> chainKey = default)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
        _chainKey = chainKey.ToArray();
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

        var cursor = new AuditChainVerificationCursor();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var link = new AuditChainLink
            {
                AuditId = reader.GetInt64(0),
                Timestamp = reader.GetFieldValue<DateTimeOffset>(1),
                EventType = ParseEnum(reader.GetString(2), AuditEventType.AdminAction),
                Actor = reader.GetString(3),
                ActorType = ParseEnum(reader.GetString(4), AuditActorType.Anonymous),
                ResourceType = reader.GetString(5),
                ResourceId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Action = reader.GetString(7),
                Outcome = ParseEnum(reader.GetString(8), AuditOutcome.Failure),
                CorrelationId = reader.GetString(9),
                RemoteIp = reader.IsDBNull(10) ? null : reader.GetString(10),
                UserAgent = reader.IsDBNull(11) ? null : reader.GetString(11),
                Details = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                PreviousHash = reader.IsDBNull(13) ? null : reader.GetString(13),
                EntryHash = reader.IsDBNull(14) ? null : reader.GetString(14),
            };

            var failure = cursor.Observe(link, _chainKey);
            if (failure is not null)
            {
                return failure;
            }
        }

        return cursor.Complete(_chainKey);
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ? parsed : fallback;
}
