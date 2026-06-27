// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.AuditLog;

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditRetentionPruner"/> backing the
/// configurable audit retention policy (#509). Removes audit records that have
/// aged past the retention window from <c>honua.audit_log</c>.
/// </summary>
/// <remarks>
/// <para>
/// The audit trail is append-only and tamper-evident: migration 033 installs an
/// <c>audit_log_no_delete</c> rule (<c>DO INSTEAD NOTHING</c>) that blocks ordinary
/// deletes, and migration 069 builds a hash chain over the rows. Retention is the
/// sanctioned, privileged maintenance path the migration explicitly allows
/// ("rotate / archive via partition swap or explicit DROP RULE in a maintenance
/// window"). This pruner performs that rotation safely:
/// </para>
/// <list type="number">
/// <item>It lifts the delete guard inside a single transaction and restores it in
/// the same transaction, so the append-only rule is reinstated on commit — and a
/// rollback (on any error) restores it too, because DDL is transactional in
/// PostgreSQL.</item>
/// <item>It deletes only a <em>contiguous prefix</em> of the chain (rows whose
/// <c>audit_id</c> precedes the oldest still-retained row). Because the integrity
/// verifier treats the first surviving hashed row as the chain genesis, pruning a
/// head prefix never breaks verification — unlike a mid-chain delete. Only expired
/// rows are removed: any row with <c>audit_id</c> below the first non-expired row's
/// id is, by construction, itself expired.</item>
/// </list>
/// </remarks>
internal sealed class PostgresAuditLogRetentionPruner : IAuditRetentionPruner
{
    private const string NoDeleteRule = "audit_log_no_delete";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAuditLogRetentionPruner> _logger;
    private readonly string _table;

    public PostgresAuditLogRetentionPruner(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAuditLogRetentionPruner> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
    }

    public async Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Unbounded retention means "retain forever" — never remove anything.
        if (!policy.IsBounded)
        {
            return 0;
        }

        var cutoff = policy.CutoffUtc(DateTimeOffset.UtcNow);

        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.Connection
            .BeginTransactionAsync(ct).ConfigureAwait(false);

        // Lift the append-only delete guard for the duration of this transaction.
        // _table is schema-validated by SchemaSearchPath; the rule name is a constant.
        await ExecuteAsync(
            connection,
            transaction,
            $"ALTER TABLE {_table} DISABLE RULE {NoDeleteRule}",
            ct).ConfigureAwait(false);

        // Delete the maximal contiguous head prefix of expired rows. keep_from is
        // the smallest audit_id among still-retained (non-expired) rows; everything
        // before it is necessarily expired and is the chain's leading segment, so
        // removing it keeps the surviving hash chain intact. When every row is
        // expired (keep_from IS NULL) the whole table is cleared.
        var deleteSql = $"""
            WITH boundary AS (
                SELECT MIN(audit_id) AS keep_from
                FROM {_table}
                WHERE timestamp >= @cutoff
            )
            DELETE FROM {_table} a
            USING boundary b
            WHERE (b.keep_from IS NOT NULL AND a.audit_id < b.keep_from)
               OR (b.keep_from IS NULL AND a.timestamp < @cutoff)
            """;

        int deleted;
        await using (var command = new NpgsqlCommand(deleteSql, connection, transaction))
        {
            command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
            deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Restore the append-only guard before committing.
        await ExecuteAsync(
            connection,
            transaction,
            $"ALTER TABLE {_table} ENABLE RULE {NoDeleteRule}",
            ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (deleted > 0)
        {
            AuditRetentionPostgresLog.Pruned(_logger, deleted, cutoff);
        }

        return deleted;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

internal static partial class AuditRetentionPostgresLog
{
    [LoggerMessage(
        EventId = 7311,
        Level = LogLevel.Information,
        Message = "Pruned {DeletedCount} audit record(s) older than {Cutoff:o} under the configured retention policy.")]
    public static partial void Pruned(ILogger logger, int deletedCount, DateTimeOffset cutoff);
}
