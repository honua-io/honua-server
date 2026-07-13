// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Observability;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Observability;

/// <summary>
/// Integration tests for <see cref="PostgresOpsHealthRollupStore"/> (#2553): the write/upsert,
/// downsample, prune, and read paths against an isolated per-test schema mirroring migration 077.
/// </summary>
[Collection("Database")]
public sealed class PostgresOpsHealthRollupStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task WriteSample_RoundTripsLatencyAndVitals()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsHealthRollupStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsHealthRollupStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var capturedAt = new DateTimeOffset(2026, 7, 7, 12, 0, 30, TimeSpan.Zero);

            await store.WriteSampleAsync(Sample("replica-a", capturedAt, ("FeatureServer", 100, 3, 12, 40, 90, 150)));

            var rows = await store.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, capturedAt.AddMinutes(-1), capturedAt.AddMinutes(1));
            rows.Should().HaveCount(1);
            rows[0].ReplicaId.Should().Be("replica-a");
            rows[0].BucketStart.Should().Be(new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));
            rows[0].Point.Protocol.Should().Be("FeatureServer");
            rows[0].Point.RequestCount.Should().Be(100);
            rows[0].Point.P50Ms.Should().Be(12);
            rows[0].Point.P95Ms.Should().Be(40);
            rows[0].Point.P99Ms.Should().Be(90);
            rows[0].Point.MaxMs.Should().Be(150);

            var vitals = await store.ReadVitalsAsync(OpsHealthRollupTier.OneMinute, capturedAt.AddMinutes(-1), capturedAt.AddMinutes(1));
            vitals.Should().HaveCount(1);
            vitals[0].Point.OverallStatus.Should().Be("Healthy");
            vitals[0].Point.GpQueueTotal.Should().Be(4);
            vitals[0].Point.GpQueueBreakdown.Should().HaveCount(2);
            vitals[0].Point.GpQueueBreakdown["Queued|local"].Should().Be(3);
            vitals[0].Point.GpQueueBreakdown["Running|local"].Should().Be(1);
            vitals[0].Point.AlertPending.Should().Be(7);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task WriteSample_SameMinute_IsLastWriteWins()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsHealthRollupStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsHealthRollupStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var minute = new DateTimeOffset(2026, 7, 7, 12, 5, 0, TimeSpan.Zero);

            await store.WriteSampleAsync(Sample("replica-a", minute.AddSeconds(5), ("FeatureServer", 10, 0, 5, 5, 5, 5)));
            await store.WriteSampleAsync(Sample("replica-a", minute.AddSeconds(50), ("FeatureServer", 999, 9, 42, 42, 42, 42)));

            var rows = await store.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, minute.AddMinutes(-1), minute.AddMinutes(1));
            rows.Should().HaveCount(1);
            rows[0].Point.RequestCount.Should().Be(999);
            rows[0].Point.P95Ms.Should().Be(42);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DownsampleAndPrune_MaterializesFiveMinuteTierWithPeakPercentiles()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsHealthRollupStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsHealthRollupStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var windowStart = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

            // Two minute buckets inside the same 5-minute window, with differing GP queue breakdowns.
            await store.WriteSampleAsync(Sample(
                "replica-a", windowStart.AddMinutes(0), ("FeatureServer", 100, 0, 10, 100, 200, 300),
                new Dictionary<string, int> { ["Queued|local"] = 5, ["Running|local"] = 2 }));
            await store.WriteSampleAsync(Sample(
                "replica-a", windowStart.AddMinutes(1), ("FeatureServer", 300, 0, 20, 200, 400, 250),
                new Dictionary<string, int> { ["Queued|local"] = 3, ["Queued|aws-batch"] = 9 }));

            var now = windowStart.AddMinutes(30);
            await store.DownsampleAndPruneAsync(now, OpsHealthRollupRetentionPolicy.Default);

            var fiveMin = await store.ReadLatencyAsync(OpsHealthRollupTier.FiveMinute, windowStart.AddMinutes(-1), now);
            fiveMin.Should().HaveCount(1);
            fiveMin[0].BucketStart.Should().Be(windowStart);
            // #2809: percentiles store the peak (MAX) rolling-window observation, never a weighted mean that
            // would average a spike away. Peak p95 = MAX(100, 200) = 200.
            fiveMin[0].Point.P95Ms.Should().Be(200);
            fiveMin[0].Point.P99Ms.Should().Be(400);
            // Counts store the peak observation.
            fiveMin[0].Point.RequestCount.Should().Be(300);
            fiveMin[0].Point.MaxMs.Should().Be(300);

            // Breakdown is downsampled with a per-key peak (max) across the coarse bucket's minutes.
            var fiveMinVitals = await store.ReadVitalsAsync(OpsHealthRollupTier.FiveMinute, windowStart.AddMinutes(-1), now);
            fiveMinVitals.Should().HaveCount(1);
            var breakdown = fiveMinVitals[0].Point.GpQueueBreakdown;
            breakdown.Should().HaveCount(3);
            breakdown["Queued|local"].Should().Be(5);
            breakdown["Running|local"].Should().Be(2);
            breakdown["Queued|aws-batch"].Should().Be(9);

            var hourly = await store.ReadVitalsAsync(OpsHealthRollupTier.Hourly, windowStart.AddMinutes(-1), now);
            hourly.Should().NotBeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DownsampleAndPrune_DropsExpiredOneMinuteRowsButKeepsHourly()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsHealthRollupStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsHealthRollupStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var now = DateTimeOffset.UtcNow;
            var stale = now.AddHours(-48);

            await store.WriteSampleAsync(Sample("replica-a", stale, ("FeatureServer", 50, 1, 10, 20, 30, 40)));

            var pruned = await store.DownsampleAndPruneAsync(now, OpsHealthRollupRetentionPolicy.Default);
            pruned.Should().BeGreaterThan(0);

            var oneMin = await store.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, stale.AddMinutes(-1), now);
            oneMin.Should().BeEmpty();

            // Hourly (retained 90d) survives the 24h one-minute prune.
            var hourly = await store.ReadLatencyAsync(OpsHealthRollupTier.Hourly, stale.AddHours(-1), now);
            hourly.Should().NotBeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ReadRecentLatency_ExcludesSelfReplica()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsHealthRollupStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsHealthRollupStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var now = DateTimeOffset.UtcNow;

            await store.WriteSampleAsync(Sample("replica-self", now, ("FeatureServer", 10, 0, 5, 5, 5, 5)));
            await store.WriteSampleAsync(Sample("replica-peer", now, ("FeatureServer", 20, 0, 6, 6, 6, 6)));

            var recent = await store.ReadRecentLatencyAsync(now.AddMinutes(-5), "replica-self");
            recent.Should().OnlyContain(row => row.ReplicaId == "replica-peer");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static OpsHealthRollupSample Sample(
        string replicaId,
        DateTimeOffset capturedAt,
        (string Protocol, long Requests, long Errors, double P50, double P95, double P99, double Max) latency,
        IReadOnlyDictionary<string, int>? gpBreakdown = null)
    {
        return new OpsHealthRollupSample
        {
            ReplicaId = replicaId,
            CapturedAt = capturedAt,
            Latency =
            [
                new OpsHealthLatencyPoint
                {
                    Protocol = latency.Protocol,
                    RequestCount = latency.Requests,
                    ErrorCount = latency.Errors,
                    P50Ms = latency.P50,
                    P95Ms = latency.P95,
                    P99Ms = latency.P99,
                    MaxMs = latency.Max,
                },
            ],
            Vitals = new OpsHealthVitalsPoint
            {
                OverallStatus = "Healthy",
                GpQueueTotal = 4,
                GpQueueBreakdown = gpBreakdown ?? new Dictionary<string, int> { ["Queued|local"] = 3, ["Running|local"] = 1 },
                AlertPending = 7,
                AlertDeadLettered = 0,
                DbPoolUtilization = 0.5,
                DbActiveConnections = 6,
                CacheHitRatio = 0.9,
                ErrorRate = 0.01,
            },
        };
    }

    private async Task EnsureSchemaAsync(string schema)
    {
        await fixture.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "{schema}".ops_health_rollup_latency (
                replica_id     TEXT             NOT NULL,
                tier           SMALLINT         NOT NULL,
                bucket_start   TIMESTAMPTZ      NOT NULL,
                protocol       TEXT             NOT NULL,
                request_count  BIGINT           NOT NULL DEFAULT 0,
                error_count    BIGINT           NOT NULL DEFAULT 0,
                p50_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
                p95_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
                p99_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
                max_ms         DOUBLE PRECISION NOT NULL DEFAULT 0,
                updated_at     TIMESTAMPTZ      NOT NULL DEFAULT NOW(),
                CONSTRAINT ops_health_rollup_latency_pk PRIMARY KEY (replica_id, tier, bucket_start, protocol),
                CONSTRAINT ops_health_rollup_latency_valid_tier CHECK (tier IN (0, 1, 2))
            );

            CREATE TABLE IF NOT EXISTS "{schema}".ops_health_rollup_vitals (
                replica_id            TEXT             NOT NULL,
                tier                  SMALLINT         NOT NULL,
                bucket_start          TIMESTAMPTZ      NOT NULL,
                overall_status        TEXT             NOT NULL DEFAULT 'Unknown',
                gp_queue_total        INTEGER          NOT NULL DEFAULT 0,
                gp_queue_breakdown    JSONB            NOT NULL DEFAULT jsonb_build_object(),
                alert_pending         BIGINT           NULL,
                alert_dead_lettered   BIGINT           NULL,
                db_pool_utilization   DOUBLE PRECISION NULL,
                db_active_connections INTEGER          NOT NULL DEFAULT 0,
                cache_hit_ratio       DOUBLE PRECISION NOT NULL DEFAULT 0,
                error_rate            DOUBLE PRECISION NOT NULL DEFAULT 0,
                updated_at            TIMESTAMPTZ      NOT NULL DEFAULT NOW(),
                CONSTRAINT ops_health_rollup_vitals_pk PRIMARY KEY (replica_id, tier, bucket_start),
                CONSTRAINT ops_health_rollup_vitals_valid_tier CHECK (tier IN (0, 1, 2))
            );
            """);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schema) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schema}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
