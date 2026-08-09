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
/// window"). This pruner performs that rotation safely and â€” critically â€”
/// <em>without holding a long-lived lock</em>:
/// </para>
/// <list type="number">
/// <item>It establishes a single head-prefix safety boundary up front
/// (<c>MIN(audit_id)</c> among still-retained rows). Everything below that
/// boundary is, by construction, an expired contiguous prefix of the chain.
/// Because the integrity verifier treats the first surviving hashed row as the
/// chain genesis, pruning a head prefix never breaks verification â€” unlike a
/// mid-chain delete. Concurrent inserts only ever add rows <em>above</em> the
/// boundary (higher <c>audit_id</c>, newer timestamps), so they can never be
/// caught by the prune and the boundary stays valid for the whole sweep.</item>
/// <item>It then deletes those expired rows in small <em>bounded chunks</em>,
/// each in its own short transaction, looping until none remain. The
/// append-only guard is lifted and restored <em>inside each batch transaction</em>
/// rather than once around the entire sweep. <c>DISABLE/ENABLE RULE</c> take an
/// <c>ACCESS EXCLUSIVE</c> lock on the table; holding that across a multi-million
/// row first sweep would block every inline audit insert (OperationGateway,
/// admin endpoints) for the sweep's full duration. Scoping the lift to a single
/// small batch means the lock is held only for milliseconds at a time and is
/// fully released between batches, so inline audit writes interleave with
/// pruning instead of stalling.</item>
/// </list>
/// <para>
/// Each batch transaction is atomic: a crash or error mid-batch rolls back and
/// restores the rule (DDL is transactional in PostgreSQL), and between batches
/// the committed state always has the guard enabled.
/// </para>
/// </remarks>
internal sealed class PostgresAuditLogRetentionPruner : IAuditRetentionPruner
{
    /// <summary>Default rows deleted per batch when no explicit size is configured.</summary>
    internal const int DefaultBatchSize = 5_000;

    private const string NoDeleteRule = "audit_log_no_delete";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAuditLogRetentionPruner> _logger;
    private readonly string _table;
    private readonly int _batchSize;

    public PostgresAuditLogRetentionPruner(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresAuditLogRetentionPruner> logger,
        string? schemaName = null,
        int batchSize = DefaultBatchSize)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _table = SchemaSearchPath.QualifyTable("audit_log", schemaName);
        _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;
    }

    public async Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Unbounded retention means "retain forever" â€” never remove anything.
        if (!policy.IsBounded)
        {
            return 0;
        }

        var cutoff = policy.CutoffUtc(DateTimeOffset.UtcNow);

        await using var connection = await _connectionProvider
            .OpenNpgsqlConnectionAsync(ct).ConfigureAwait(false);

        // Establish the head-prefix safety boundary once, in a short read. keep_from
        // is the smallest audit_id among still-retained (non-expired) rows; every
        // row below it is necessarily expired and forms the chain's leading segment,
        // so removing it keeps the surviving hash chain intact. A NULL result means
        // no row is still retained (the whole table is expired) and everything older
        // than the cutoff may go. Concurrent inserts only add rows above this
        // boundary, so it remains correct for the duration of the chunked sweep.
        var keepFrom = await ReadKeepFromAsync(connection, cutoff, ct).ConfigureAwait(false);

        var totalDeleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var deleted = await DeleteChunkAsync(connection, cutoff, keepFrom, ct).ConfigureAwait(false);
            totalDeleted += deleted;

            // A short batch (including zero) means the expired prefix is exhausted.
            if (deleted < _batchSize)
            {
                break;
            }
        }

        if (totalDeleted > 0)
        {
            AuditRetentionPostgresLog.Pruned(_logger, totalDeleted, cutoff);
        }

        return totalDeleted;
    }

    private async Task<long?> ReadKeepFromAsync(NpgsqlConnection connection, DateTimeOffset cutoff, CancellationToken ct)
    {
        var sql = $"SELECT MIN(audit_id) FROM {_table} WHERE timestamp >= @cutoff";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : (long)result;
    }

    private async Task<int> DeleteChunkAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoff,
        long? keepFrom,
        CancellationToken ct)
    {
        // Each batch runs in its own short transaction so the ACCESS EXCLUSIVE lock
        // taken by DISABLE/ENABLE RULE is held only for the few milliseconds this
        // small delete takes, then released â€” letting inline audit inserts proceed
        // between batches instead of stalling for the whole sweep.
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // _table is schema-validated by SchemaSearchPath; the rule name is a constant.
        await ExecuteAsync(
            connection,
            transaction,
            $"ALTER TABLE {_table} DISABLE RULE {NoDeleteRule}",
            ct).ConfigureAwait(false);

        // Delete a bounded slice of the expired head prefix. The inner SELECT picks
        // the oldest expired rows (ORDER BY audit_id) up to the batch size and the
        // outer DELETE removes exactly those ctids. When keep_from is known we bound
        // by audit_id < keep_from (the proven-expired prefix); otherwise the whole
        // table is expired and we bound by timestamp < cutoff.
        var boundaryPredicate = keepFrom.HasValue
            ? "audit_id < @keepFrom"
            : "timestamp < @cutoff";

        var deleteSql = $"""
            DELETE FROM {_table}
            WHERE ctid IN (
                SELECT ctid
                FROM {_table}
                WHERE {boundaryPredicate}
                ORDER BY audit_id
                LIMIT @batchSize
            )
            """;

        int deleted;
        await using (var command = new NpgsqlCommand(deleteSql, connection, transaction))
        {
            if (keepFrom.HasValue)
            {
                command.Parameters.AddWithValue("keepFrom", NpgsqlDbType.Bigint, keepFrom.Value);
            }
            else
            {
                command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
            }

            command.Parameters.AddWithValue("batchSize", NpgsqlDbType.Integer, _batchSize);
            deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Restore the append-only guard before committing this batch.
        await ExecuteAsync(
            connection,
            transaction,
            $"ALTER TABLE {_table} ENABLE RULE {NoDeleteRule}",
            ct).ConfigureAwait(false);

        await transaction.CommitSafelyAsync(ct).ConfigureAwait(false);

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
