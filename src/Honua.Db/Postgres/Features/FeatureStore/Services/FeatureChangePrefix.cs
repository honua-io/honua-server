// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Diagnostics;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

// Advances the committed feature-change prefix (migration 228) that
// honua.feature_change_read_horizon() returns. Readers advance it themselves, so the horizon
// never depends on a background publisher being hosted for the database.
internal static class FeatureChangePrefix
{
    // How long a read waits for feature writers that were already in flight when it started.
    // Edits take milliseconds; a long import holds the prefix back and the read then returns
    // the older, still-safe horizon.
    internal static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(2);

    // The wait for a realtime read, which is woken again by the next commit's notification.
    internal static readonly TimeSpan CatchUpBudget = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan _maxBackoff = TimeSpan.FromMilliseconds(50);

    private const string AllocatedSql =
        "SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM honua.sync_generation";

    // One publication. Needs a connection with no open transaction.
    public static async Task<long> PublishAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        // The publisher rejects any transaction that already owns a transaction id or uses a
        // stronger isolation level, so each call runs in its own READ COMMITTED transaction.
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT honua.publish_feature_change_prefix()", connection, transaction);
        var published = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }

    // Publishes until the prefix covers every generation allocated before this call, or until
    // SettleBudget elapses, and returns the published prefix. An explicit target is a caller's own
    // committed rows; that wait has no budget, because a lower prefix would make the caller record a
    // position before its own changes. The old global writer lock blocked such a caller for as long.
    public static Task<long> SettleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        => SettleAsync(connection, target: null, cancellationToken);

    public static async Task<long> SettleAsync(NpgsqlConnection connection, long? target, CancellationToken cancellationToken)
    {
        var through = target ?? await ReadAllocatedAsync(connection, cancellationToken).ConfigureAwait(false);
        var budget = target is null ? SettleBudget : Timeout.InfiniteTimeSpan;
        return await PublishUntilAsync(connection, new SettleWindow(through, budget), cancellationToken).ConfigureAwait(false);
    }

    // A realtime head or cursor read: wait at most CatchUpBudget for writers already in flight,
    // so a read that lands during a short edit does not leave that edit for the next poll.
    public static async Task<long> CatchUpAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var through = await ReadAllocatedAsync(connection, cancellationToken).ConfigureAwait(false);
        return await PublishUntilAsync(connection, new SettleWindow(through, CatchUpBudget), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> PublishUntilAsync(
        NpgsqlConnection connection,
        SettleWindow window,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var backoff = TimeSpan.FromMilliseconds(2);
        var published = await PublishAsync(connection, cancellationToken).ConfigureAwait(false);
        while (published < window.Through && window.IsOpen(Stopwatch.GetElapsedTime(started)))
        {
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = backoff * 2 < _maxBackoff ? backoff * 2 : _maxBackoff;
            published = await PublishAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        return published;
    }

    private readonly record struct SettleWindow(long Through, TimeSpan Budget)
    {
        public bool IsOpen(TimeSpan elapsed) => Budget == Timeout.InfiniteTimeSpan || elapsed < Budget;
    }

    // Settles, then reads honua.feature_change_read_horizon(), the one definition of the horizon.
    public static Task<long> SettleAndReadHorizonAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        => SettleAndReadHorizonAsync(connection, target: null, cancellationToken);

    public static async Task<long> SettleAndReadHorizonAsync(
        NpgsqlConnection connection,
        long? target,
        CancellationToken cancellationToken)
    {
        _ = await SettleAsync(connection, target, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT honua.feature_change_read_horizon()", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<long> ReadAllocatedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AllocatedSql, connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
