// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Observability;

/// <summary>
/// PostgreSQL implementation of <see cref="IOpsHealthRollupStore"/> (#2553). Persists per-replica
/// ops-health flushes into two bounded rollup tables (serving latency and vitals), downsamples the
/// 5-minute and hourly tiers from the 1-minute tier, and prunes each tier to its retention cap.
/// </summary>
internal sealed class PostgresOpsHealthRollupStore : IOpsHealthRollupStore
{
    // Tier discriminators persisted in the smallint `tier` column.
    private const short TierOneMinute = 0;
    private const short TierFiveMinute = 1;
    private const short TierHourly = 2;

    private const int FiveMinuteSeconds = 300;
    private const int HourSeconds = 3600;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _latencyTable;
    private readonly string _vitalsTable;

    public PostgresOpsHealthRollupStore(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _latencyTable = SchemaSearchPath.QualifyTable("ops_health_rollup_latency", schemaName);
        _vitalsTable = SchemaSearchPath.QualifyTable("ops_health_rollup_vitals", schemaName);
    }

    public async Task WriteSampleAsync(OpsHealthRollupSample sample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(sample.ReplicaId);

        var bucketStart = TruncateToMinute(sample.CapturedAt);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var latencySql = $"""
            INSERT INTO {_latencyTable}
                (replica_id, tier, bucket_start, protocol, request_count, error_count, p50_ms, p95_ms, p99_ms, max_ms, distribution, updated_at)
            VALUES (@replica_id, @tier, @bucket_start, @protocol, @request_count, @error_count, @p50_ms, @p95_ms, @p99_ms, @max_ms, @distribution, @updated_at)
            ON CONFLICT (replica_id, tier, bucket_start, protocol) DO UPDATE SET
                request_count = EXCLUDED.request_count,
                error_count = EXCLUDED.error_count,
                p50_ms = EXCLUDED.p50_ms,
                p95_ms = EXCLUDED.p95_ms,
                p99_ms = EXCLUDED.p99_ms,
                max_ms = EXCLUDED.max_ms,
                distribution = EXCLUDED.distribution,
                updated_at = EXCLUDED.updated_at
            """;

        foreach (var point in sample.Latency)
        {
            await using var command = new NpgsqlCommand(latencySql, connection, transaction);
            command.Parameters.AddWithValue("replica_id", NpgsqlDbType.Text, sample.ReplicaId);
            command.Parameters.AddWithValue("tier", NpgsqlDbType.Smallint, TierOneMinute);
            command.Parameters.AddWithValue("bucket_start", NpgsqlDbType.TimestampTz, bucketStart);
            command.Parameters.AddWithValue("protocol", NpgsqlDbType.Text, point.Protocol);
            command.Parameters.AddWithValue("request_count", NpgsqlDbType.Bigint, point.RequestCount);
            command.Parameters.AddWithValue("error_count", NpgsqlDbType.Bigint, point.ErrorCount);
            command.Parameters.AddWithValue("p50_ms", NpgsqlDbType.Double, point.P50Ms);
            command.Parameters.AddWithValue("p95_ms", NpgsqlDbType.Double, point.P95Ms);
            command.Parameters.AddWithValue("p99_ms", NpgsqlDbType.Double, point.P99Ms);
            command.Parameters.AddWithValue("max_ms", NpgsqlDbType.Double, point.MaxMs);
            command.Parameters.AddWithValue("distribution", NpgsqlDbType.Jsonb, (object?)SerializeDistribution(point.Distribution) ?? DBNull.Value);
            command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var vitalsSql = $"""
            INSERT INTO {_vitalsTable}
                (replica_id, tier, bucket_start, overall_status, gp_queue_total, gp_queue_breakdown, alert_pending, alert_dead_lettered,
                 db_pool_utilization, db_active_connections, cache_hit_ratio, error_rate, updated_at)
            VALUES (@replica_id, @tier, @bucket_start, @overall_status, @gp_queue_total, @gp_queue_breakdown, @alert_pending, @alert_dead_lettered,
                    @db_pool_utilization, @db_active_connections, @cache_hit_ratio, @error_rate, @updated_at)
            ON CONFLICT (replica_id, tier, bucket_start) DO UPDATE SET
                overall_status = EXCLUDED.overall_status,
                gp_queue_total = EXCLUDED.gp_queue_total,
                gp_queue_breakdown = EXCLUDED.gp_queue_breakdown,
                alert_pending = EXCLUDED.alert_pending,
                alert_dead_lettered = EXCLUDED.alert_dead_lettered,
                db_pool_utilization = EXCLUDED.db_pool_utilization,
                db_active_connections = EXCLUDED.db_active_connections,
                cache_hit_ratio = EXCLUDED.cache_hit_ratio,
                error_rate = EXCLUDED.error_rate,
                updated_at = EXCLUDED.updated_at
            """;

        await using (var vitalsCommand = new NpgsqlCommand(vitalsSql, connection, transaction))
        {
            var vitals = sample.Vitals;
            vitalsCommand.Parameters.AddWithValue("replica_id", NpgsqlDbType.Text, sample.ReplicaId);
            vitalsCommand.Parameters.AddWithValue("tier", NpgsqlDbType.Smallint, TierOneMinute);
            vitalsCommand.Parameters.AddWithValue("bucket_start", NpgsqlDbType.TimestampTz, bucketStart);
            vitalsCommand.Parameters.AddWithValue("overall_status", NpgsqlDbType.Text, vitals.OverallStatus);
            vitalsCommand.Parameters.AddWithValue("gp_queue_total", NpgsqlDbType.Integer, vitals.GpQueueTotal);
            vitalsCommand.Parameters.AddWithValue("gp_queue_breakdown", NpgsqlDbType.Jsonb, SerializeBreakdown(vitals.GpQueueBreakdown));
            vitalsCommand.Parameters.AddWithValue("alert_pending", NpgsqlDbType.Bigint, (object?)vitals.AlertPending ?? DBNull.Value);
            vitalsCommand.Parameters.AddWithValue("alert_dead_lettered", NpgsqlDbType.Bigint, (object?)vitals.AlertDeadLettered ?? DBNull.Value);
            vitalsCommand.Parameters.AddWithValue("db_pool_utilization", NpgsqlDbType.Double, (object?)vitals.DbPoolUtilization ?? DBNull.Value);
            vitalsCommand.Parameters.AddWithValue("db_active_connections", NpgsqlDbType.Integer, vitals.DbActiveConnections);
            vitalsCommand.Parameters.AddWithValue("cache_hit_ratio", NpgsqlDbType.Double, vitals.CacheHitRatio);
            vitalsCommand.Parameters.AddWithValue("error_rate", NpgsqlDbType.Double, vitals.ErrorRate);
            vitalsCommand.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            _ = await vitalsCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DownsampleAndPruneAsync(DateTimeOffset now, OpsHealthRollupRetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await DownsampleLatencyAsync(connection, transaction, TierFiveMinute, FiveMinuteSeconds, now, cancellationToken).ConfigureAwait(false);
        await DownsampleLatencyAsync(connection, transaction, TierHourly, HourSeconds, now, cancellationToken).ConfigureAwait(false);
        await DownsampleVitalsAsync(connection, transaction, TierFiveMinute, FiveMinuteSeconds, now, cancellationToken).ConfigureAwait(false);
        await DownsampleVitalsAsync(connection, transaction, TierHourly, HourSeconds, now, cancellationToken).ConfigureAwait(false);

        var pruned = 0;
        pruned += await PruneAsync(connection, transaction, _latencyTable, TierOneMinute, now - policy.OneMinuteRetention, cancellationToken).ConfigureAwait(false);
        pruned += await PruneAsync(connection, transaction, _latencyTable, TierFiveMinute, now - policy.FiveMinuteRetention, cancellationToken).ConfigureAwait(false);
        pruned += await PruneAsync(connection, transaction, _latencyTable, TierHourly, now - policy.HourlyRetention, cancellationToken).ConfigureAwait(false);
        pruned += await PruneAsync(connection, transaction, _vitalsTable, TierOneMinute, now - policy.OneMinuteRetention, cancellationToken).ConfigureAwait(false);
        pruned += await PruneAsync(connection, transaction, _vitalsTable, TierFiveMinute, now - policy.FiveMinuteRetention, cancellationToken).ConfigureAwait(false);
        pruned += await PruneAsync(connection, transaction, _vitalsTable, TierHourly, now - policy.HourlyRetention, cancellationToken).ConfigureAwait(false);

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return pruned;
    }

    public Task<IReadOnlyList<OpsHealthLatencyRow>> ReadLatencyAsync(OpsHealthRollupTier tier, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        => ReadLatencyCoreAsync(ToTier(tier), from, to, excludeReplicaId: null, cancellationToken);

    public Task<IReadOnlyList<OpsHealthLatencyRow>> ReadRecentLatencyAsync(DateTimeOffset since, string? excludeReplicaId, CancellationToken cancellationToken = default)
        => ReadLatencyCoreAsync(TierOneMinute, since, DateTimeOffset.UtcNow.AddMinutes(1), excludeReplicaId, cancellationToken);

    public async Task<IReadOnlyList<OpsHealthVitalsRow>> ReadVitalsAsync(OpsHealthRollupTier tier, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT replica_id, bucket_start, overall_status, gp_queue_total, alert_pending, alert_dead_lettered,
                   db_pool_utilization, db_active_connections, cache_hit_ratio, error_rate, gp_queue_breakdown
            FROM {_vitalsTable}
            WHERE tier = @tier AND bucket_start >= @from AND bucket_start <= @to
            ORDER BY bucket_start, replica_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tier", NpgsqlDbType.Smallint, ToTier(tier));
        command.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from);
        command.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to);

        var rows = new List<OpsHealthVitalsRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new OpsHealthVitalsRow
            {
                ReplicaId = reader.GetString(0),
                BucketStart = reader.GetFieldValue<DateTimeOffset>(1),
                Point = new OpsHealthVitalsPoint
                {
                    OverallStatus = reader.GetString(2),
                    GpQueueTotal = reader.GetInt32(3),
                    AlertPending = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    AlertDeadLettered = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    DbPoolUtilization = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    DbActiveConnections = reader.GetInt32(7),
                    CacheHitRatio = reader.GetDouble(8),
                    ErrorRate = reader.GetDouble(9),
                    GpQueueBreakdown = ParseBreakdown(reader.IsDBNull(10) ? null : reader.GetString(10)),
                },
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<OpsHealthLatencyRow>> ReadLatencyCoreAsync(short tier, DateTimeOffset from, DateTimeOffset to, string? excludeReplicaId, CancellationToken cancellationToken)
    {
        var filter = string.IsNullOrWhiteSpace(excludeReplicaId) ? string.Empty : " AND replica_id <> @exclude_replica_id";
        var sql = $"""
            SELECT replica_id, bucket_start, protocol, request_count, error_count, p50_ms, p95_ms, p99_ms, max_ms, distribution
            FROM {_latencyTable}
            WHERE tier = @tier AND bucket_start >= @from AND bucket_start <= @to{filter}
            ORDER BY bucket_start, protocol, replica_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tier", NpgsqlDbType.Smallint, tier);
        command.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from);
        command.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to);
        if (!string.IsNullOrWhiteSpace(excludeReplicaId))
        {
            command.Parameters.AddWithValue("exclude_replica_id", NpgsqlDbType.Text, excludeReplicaId);
        }

        var rows = new List<OpsHealthLatencyRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new OpsHealthLatencyRow
            {
                ReplicaId = reader.GetString(0),
                BucketStart = reader.GetFieldValue<DateTimeOffset>(1),
                Point = new OpsHealthLatencyPoint
                {
                    Protocol = reader.GetString(2),
                    RequestCount = reader.GetInt64(3),
                    ErrorCount = reader.GetInt64(4),
                    P50Ms = reader.GetDouble(5),
                    P95Ms = reader.GetDouble(6),
                    P99Ms = reader.GetDouble(7),
                    MaxMs = reader.GetDouble(8),
                    Distribution = ParseDistribution(reader.IsDBNull(9) ? null : reader.GetString(9)),
                },
            });
        }

        return rows;
    }

    // Downsamples the 1-minute latency tier into a coarser tier using an epoch-floor bucket (UTC-stable,
    // independent of the session TimeZone). Percentiles use a request-count-weighted mean; counts store the
    // peak (max) rolling-window observation within the coarse bucket. Idempotent via upsert.
    private async Task DownsampleLatencyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, short targetTier, int bucketSeconds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_latencyTable}
                (replica_id, tier, bucket_start, protocol, request_count, error_count, p50_ms, p95_ms, p99_ms, max_ms, updated_at)
            SELECT
                replica_id,
                @target_tier,
                to_timestamp(floor(extract(epoch FROM bucket_start) / @bucket_seconds) * @bucket_seconds),
                protocol,
                MAX(request_count),
                MAX(error_count),
                CASE WHEN SUM(request_count) > 0 THEN SUM(p50_ms * request_count) / SUM(request_count) ELSE MAX(p50_ms) END,
                CASE WHEN SUM(request_count) > 0 THEN SUM(p95_ms * request_count) / SUM(request_count) ELSE MAX(p95_ms) END,
                CASE WHEN SUM(request_count) > 0 THEN SUM(p99_ms * request_count) / SUM(request_count) ELSE MAX(p99_ms) END,
                MAX(max_ms),
                @updated_at
            FROM {_latencyTable}
            WHERE tier = @source_tier
            GROUP BY replica_id, to_timestamp(floor(extract(epoch FROM bucket_start) / @bucket_seconds) * @bucket_seconds), protocol
            ON CONFLICT (replica_id, tier, bucket_start, protocol) DO UPDATE SET
                request_count = EXCLUDED.request_count,
                error_count = EXCLUDED.error_count,
                p50_ms = EXCLUDED.p50_ms,
                p95_ms = EXCLUDED.p95_ms,
                p99_ms = EXCLUDED.p99_ms,
                max_ms = EXCLUDED.max_ms,
                updated_at = EXCLUDED.updated_at
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("target_tier", NpgsqlDbType.Smallint, targetTier);
        command.Parameters.AddWithValue("source_tier", NpgsqlDbType.Smallint, TierOneMinute);
        command.Parameters.AddWithValue("bucket_seconds", NpgsqlDbType.Integer, bucketSeconds);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownsampleVitalsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, short targetTier, int bucketSeconds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The GP queue breakdown map is downsampled with a per-key MAX (peak depth per status|backend key
        // within the coarse bucket), consistent with the peak-counts semantics used for the scalar columns.
        var sql = $"""
            WITH source AS (
                SELECT replica_id,
                       to_timestamp(floor(extract(epoch FROM bucket_start) / @bucket_seconds) * @bucket_seconds) AS bucket,
                       overall_status, gp_queue_total, gp_queue_breakdown, alert_pending, alert_dead_lettered,
                       db_pool_utilization, db_active_connections, cache_hit_ratio, error_rate
                FROM {_vitalsTable}
                WHERE tier = @source_tier
            ),
            breakdown AS (
                SELECT replica_id, bucket, jsonb_object_agg(key, peak) AS gp_queue_breakdown
                FROM (
                    SELECT s.replica_id, s.bucket, kv.key, MAX((kv.value)::int) AS peak
                    FROM source s
                    CROSS JOIN LATERAL jsonb_each_text(s.gp_queue_breakdown) AS kv
                    GROUP BY s.replica_id, s.bucket, kv.key
                ) per_key
                GROUP BY replica_id, bucket
            )
            INSERT INTO {_vitalsTable}
                (replica_id, tier, bucket_start, overall_status, gp_queue_total, gp_queue_breakdown, alert_pending, alert_dead_lettered,
                 db_pool_utilization, db_active_connections, cache_hit_ratio, error_rate, updated_at)
            SELECT
                agg.replica_id,
                @target_tier,
                agg.bucket,
                agg.overall_status,
                agg.gp_queue_total,
                COALESCE(b.gp_queue_breakdown, jsonb_build_object()),
                agg.alert_pending,
                agg.alert_dead_lettered,
                agg.db_pool_utilization,
                agg.db_active_connections,
                agg.cache_hit_ratio,
                agg.error_rate,
                @updated_at
            FROM (
                SELECT
                    replica_id,
                    bucket,
                    CASE
                        WHEN bool_or(overall_status = 'Unhealthy') THEN 'Unhealthy'
                        WHEN bool_or(overall_status = 'Degraded') THEN 'Degraded'
                        WHEN bool_or(overall_status = 'Healthy') THEN 'Healthy'
                        ELSE MAX(overall_status)
                    END AS overall_status,
                    MAX(gp_queue_total) AS gp_queue_total,
                    MAX(alert_pending) AS alert_pending,
                    MAX(alert_dead_lettered) AS alert_dead_lettered,
                    AVG(db_pool_utilization) AS db_pool_utilization,
                    MAX(db_active_connections) AS db_active_connections,
                    AVG(cache_hit_ratio) AS cache_hit_ratio,
                    MAX(error_rate) AS error_rate
                FROM source
                GROUP BY replica_id, bucket
            ) agg
            LEFT JOIN breakdown b ON b.replica_id = agg.replica_id AND b.bucket = agg.bucket
            ON CONFLICT (replica_id, tier, bucket_start) DO UPDATE SET
                overall_status = EXCLUDED.overall_status,
                gp_queue_total = EXCLUDED.gp_queue_total,
                gp_queue_breakdown = EXCLUDED.gp_queue_breakdown,
                alert_pending = EXCLUDED.alert_pending,
                alert_dead_lettered = EXCLUDED.alert_dead_lettered,
                db_pool_utilization = EXCLUDED.db_pool_utilization,
                db_active_connections = EXCLUDED.db_active_connections,
                cache_hit_ratio = EXCLUDED.cache_hit_ratio,
                error_rate = EXCLUDED.error_rate,
                updated_at = EXCLUDED.updated_at
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("target_tier", NpgsqlDbType.Smallint, targetTier);
        command.Parameters.AddWithValue("source_tier", NpgsqlDbType.Smallint, TierOneMinute);
        command.Parameters.AddWithValue("bucket_seconds", NpgsqlDbType.Integer, bucketSeconds);
        command.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> PruneAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, short tier, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var sql = $"DELETE FROM {table} WHERE tier = @tier AND bucket_start < @cutoff";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tier", NpgsqlDbType.Smallint, tier);
        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Manual (reflection-free) JSON shaping for the flat {"<status>|<backend>": count} breakdown map,
    // keeping the store AOT-safe without a serializer context.
    private static string SerializeBreakdown(IReadOnlyDictionary<string, int> breakdown)
    {
        if (breakdown.Count == 0)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in breakdown)
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyDictionary<string, int> ParseBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return EmptyBreakdown;
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        // Not rewritten as .Where(...): TryGetInt32 does the "is it a valid int" check and the value
        // fetch in a single call; a LINQ filter would need a second conversion attempt to get the value.
        foreach (var entry in document.RootElement.EnumerateObject()
                     .Select(property => (
                         property.Name,
                         Count: property.Value.TryGetInt32(out var count) ? (int?)count : null))
                     .Where(entry => entry.Count.HasValue))
        {
            result[entry.Name] = entry.Count.GetValueOrDefault();
        }

        return result;
    }

    // Serializes the mergeable latency distribution as a compact JSON array of per-bucket counts (#2809),
    // AOT-safe with no serializer context. Returns null when the point carries no sketch so the column stays
    // NULL and the merge falls back to the weighted-mean approximation.
    private static string? SerializeDistribution(LatencyDistribution? distribution)
    {
        if (distribution is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var count in distribution.BucketCounts)
            {
                writer.WriteNumberValue(count);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static LatencyDistribution? ParseDistribution(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var counts = new long[LatencyDistribution.BucketCount];
        var index = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (index >= counts.Length)
            {
                // Length mismatch (bucket layout changed): treat as unavailable rather than misaligned.
                return null;
            }

            counts[index++] = element.TryGetInt64(out var value) ? value : 0;
        }

        // Only accept an exactly-sized vector; a short array means a layout change we cannot safely realign.
        return index == counts.Length ? new LatencyDistribution { BucketCounts = counts } : null;
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyBreakdown =
        new Dictionary<string, int>(capacity: 0);

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private static short ToTier(OpsHealthRollupTier tier) => tier switch
    {
        OpsHealthRollupTier.OneMinute => TierOneMinute,
        OpsHealthRollupTier.FiveMinute => TierFiveMinute,
        OpsHealthRollupTier.Hourly => TierHourly,
        _ => TierOneMinute,
    };
}
