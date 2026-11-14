// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.ControlPlane;
using Npgsql;

namespace Honua.Infrastructure.RateLimiting;

// Admission is an atomic database operation, never a configuration-generation cache entry.
internal sealed class PostgresRateLimitCounterStore(JobStoreDatabase database)
{
    private long _nextPruneTicks;

    public async Task<RateLimitResult> AdmitAsync(string key, ResolvedRateLimit resolved, CancellationToken cancellationToken)
    {
        var windowTicks = Math.Max(TimeSpan.TicksPerSecond, resolved.Window.Ticks);
        var counterKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            + ":" + windowTicks.ToString(CultureInfo.InvariantCulture);
        await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
        return await database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {database.Table("rate_limit_counters")} AS counters
                    (counter_key, request_count, window_reset)
                VALUES (@key, 1, to_timestamp(
                    (floor(extract(epoch FROM statement_timestamp()) / @seconds) + 1) * @seconds))
                ON CONFLICT (counter_key) DO UPDATE SET
                    request_count = CASE WHEN counters.window_reset <= statement_timestamp() THEN 1
                        ELSE LEAST(counters.request_count + 1, 2147483648::bigint) END,
                    window_reset = CASE WHEN counters.window_reset <= statement_timestamp()
                        THEN EXCLUDED.window_reset ELSE counters.window_reset END
                RETURNING request_count, window_reset
                """, connection);
            command.Parameters.AddWithValue("key", counterKey);
            command.Parameters.AddWithValue("seconds", TimeSpan.FromTicks(windowTicks).TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            var count = reader.GetInt64(0);
            return new RateLimitResult
            {
                IsAllowed = count <= resolved.Limit,
                RequestsRemaining = (int)Math.Max(0, resolved.Limit - count),
                RequestCount = (int)Math.Min(int.MaxValue, count),
                Limit = resolved.Limit,
                WindowReset = new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero)
            };
        }, StatementRepeat.NotAfterSend, cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var next = Interlocked.Read(ref _nextPruneTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextPruneTicks, now + TimeSpan.TicksPerSecond, next) != next)
        {
            return;
        }

        // Bound maintenance work per node; locked or active counters are never removed.
        await database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                DELETE FROM {database.Table("rate_limit_counters")} WHERE counter_key IN (
                    SELECT counter_key FROM {database.Table("rate_limit_counters")}
                    WHERE window_reset <= statement_timestamp()
                    ORDER BY window_reset LIMIT 256 FOR UPDATE SKIP LOCKED)
                """, connection);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }
}
