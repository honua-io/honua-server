// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.AuditLog;

internal sealed class PostgresAuditLogRetentionPruner : IAuditRetentionPruner
{
    internal const int DefaultBatchSize = 5_000;
    internal static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(2);
    internal const int DefaultMaxLockRetries = 3;
    internal static readonly TimeSpan DefaultLockBackoffBaseDelay = TimeSpan.FromMilliseconds(250);

    private const string NoDeleteRule = "audit_log_no_delete";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresAuditLogRetentionPruner> _logger;
    private readonly string _table;
    private readonly int _batchSize;

    internal LockBackoffOptions LockOptions { get; init; } = LockBackoffOptions.Default;

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

        // Unbounded retention means "retain forever" — never remove anything.
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
        var boundary = new PruneBoundary(cutoff, await ReadKeepFromAsync(connection, cutoff, ct).ConfigureAwait(false));

        var totalDeleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var deleted = await ExecuteWithLockBackoffAsync(
                token => DeleteChunkAsync(connection, boundary, token),
                LockOptions,
                ct).ConfigureAwait(false);
            if (!deleted.HasValue)
            {
                AuditRetentionPostgresLog.LockContentionDeferred(_logger, _table, totalDeleted);
                break;
            }

            totalDeleted += deleted.Value;

            // A short batch (including zero) means the expired prefix is exhausted.
            if (deleted.Value < _batchSize)
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

    internal static bool IsLockNotAvailable(PostgresException ex)
        => string.Equals(ex.SqlState, PostgresErrorCodes.LockNotAvailable, StringComparison.Ordinal);

    internal static string BuildLockTimeoutSql(TimeSpan lockTimeout)
    {
        var timeoutMs = Math.Max(1, (long)Math.Ceiling(lockTimeout.TotalMilliseconds));
        return timeoutMs % 1000 == 0
            ? $"SET LOCAL lock_timeout = '{timeoutMs / 1000}s'"
            : $"SET LOCAL lock_timeout = '{timeoutMs}ms'";
    }

    internal static async Task<int?> ExecuteWithLockBackoffAsync(
        Func<CancellationToken, Task<int>> deleteBatch,
        LockBackoffOptions options,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(0, options.MaxRetries) + 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await deleteBatch(ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsLockNotAvailable(ex))
            {
                if (attempt == maxAttempts)
                {
                    return null;
                }

                await options.DelayAsync(ComputeBackoffDelay(options.BaseDelay, attempt), ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    internal static TimeSpan ComputeBackoffDelay(TimeSpan baseDelay, int attempt)
    {
        var multiplier = 1 << Math.Min(attempt - 1, 10);
        return TimeSpan.FromMilliseconds(Math.Max(0, baseDelay.TotalMilliseconds * multiplier));
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
        PruneBoundary boundary,
        CancellationToken ct)
    {
        // Each batch runs in its own short transaction with a tight lock_timeout so
        // DISABLE RULE never queues an ACCESS EXCLUSIVE lock behind long-running reads
        // and stalls concurrent audited writes.
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tx = new PruneTransaction(connection, transaction);
        try
        {
            await ExecuteAsync(tx, BuildLockTimeoutSql(LockOptions.LockTimeout), ct).ConfigureAwait(false);

            // _table is schema-validated by SchemaSearchPath; the rule name is a constant.
            await ExecuteAsync(tx, $"ALTER TABLE {_table} DISABLE RULE {NoDeleteRule}", ct).ConfigureAwait(false);

            var deleted = await ExecuteDeleteBatchAsync(tx, boundary, ct).ConfigureAwait(false);

            // Restore the append-only guard before committing this batch.
            await ExecuteAsync(tx, $"ALTER TABLE {_table} ENABLE RULE {NoDeleteRule}", ct).ConfigureAwait(false);

            await transaction.CommitSafelyAsync(ct).ConfigureAwait(false);
            return deleted;
        }
        catch (PostgresException ex) when (IsLockNotAvailable(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> ExecuteDeleteBatchAsync(
        PruneTransaction tx,
        PruneBoundary boundary,
        CancellationToken ct)
    {
        var boundaryPredicate = boundary.KeepFrom.HasValue
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

        await using var command = new NpgsqlCommand(deleteSql, tx.Connection, tx.Transaction);
        if (boundary.KeepFrom.HasValue)
        {
            command.Parameters.AddWithValue("keepFrom", NpgsqlDbType.Bigint, boundary.KeepFrom.Value);
        }
        else
        {
            command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, boundary.Cutoff);
        }

        command.Parameters.AddWithValue("batchSize", NpgsqlDbType.Integer, _batchSize);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(PruneTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private readonly record struct PruneBoundary(DateTimeOffset Cutoff, long? KeepFrom);

    private readonly record struct PruneTransaction(NpgsqlConnection Connection, NpgsqlTransaction Transaction);

    internal readonly record struct LockBackoffOptions
    {
        public TimeSpan LockTimeout { get; init; }
        public int MaxRetries { get; init; }
        public TimeSpan BaseDelay { get; init; }
        public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; }

        public static LockBackoffOptions Default { get; } = new()
        {
            LockTimeout = DefaultLockTimeout,
            MaxRetries = DefaultMaxLockRetries,
            BaseDelay = DefaultLockBackoffBaseDelay,
            DelayAsync = static (delay, token) => Task.Delay(delay, token),
        };
    }
}

internal static partial class AuditRetentionPostgresLog
{
    [LoggerMessage(
        EventId = 7311,
        Level = LogLevel.Information,
        Message = "Pruned {DeletedCount} audit record(s) older than {Cutoff:o} under the configured retention policy.")]
    public static partial void Pruned(ILogger logger, int deletedCount, DateTimeOffset cutoff);

    [LoggerMessage(
        EventId = 7312,
        Level = LogLevel.Warning,
        Message = "Deferred remaining audit retention pruning on {Table} after repeated lock timeouts (deleted {DeletedCount} record(s) so far).")]
    public static partial void LockContentionDeferred(ILogger logger, string table, int deletedCount);
}
