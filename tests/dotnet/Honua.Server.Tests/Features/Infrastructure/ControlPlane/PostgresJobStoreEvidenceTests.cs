// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// Measurements quoted in ADR-0081 and its pull request: write volume under heartbeat load, and
// the query plans of the claim, list and reconciler statements on a seeded table. They take
// minutes and print their result, so they run only when asked for.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobStoreEvidenceTests(DatabaseFixtureAdapter database, ITestOutputHelper output)
    : IClassFixture<DatabaseFixtureAdapter>
{
    private const string EvidenceSwitch = "HONUA_JOB_STORE_EVIDENCE";
    private const int RunningJobs = 200;
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _reconcilerPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _loadDuration = TimeSpan.FromMinutes(3);

    [RequiredEnvironmentFact(EvidenceSwitch)]
    [Trait("Tier", "Slow")]
    public async Task HeartbeatLoad_200RunningJobsFor3Minutes_ReportsRowVersionsAndGrowth()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresJobStoreEvidenceTests));
        var operationIds = await StartRunningJobsAsync(harness);

        await harness.ExecuteAsync($"VACUUM ANALYZE {harness.JobsTable}");
        var before = await MeasureAsync(harness);
        var heartbeats = 0;
        var candidateQueries = 0;
        var sweeps = 0;
        var started = DateTimeOffset.UtcNow;
        var nextHeartbeat = started + _heartbeatInterval;
        while (DateTimeOffset.UtcNow - started < _loadDuration)
        {
            await Task.Delay(_reconcilerPollInterval);
            (await harness.JobStore.ListBackendReconciliationCandidateIdsAsync(CancellationToken.None)).Should().HaveCount(RunningJobs);
            candidateQueries++;
            if (DateTimeOffset.UtcNow < nextHeartbeat)
            {
                continue;
            }

            nextHeartbeat += _heartbeatInterval;
            foreach (var operationId in operationIds)
            {
                var job = (await harness.JobStore.GetAsync(operationId))!;
                (await harness.JobStore.TrySetAsync(job with { LastHeartbeatAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }))
                    .Should().BeTrue();
                heartbeats++;
            }

            await harness.RunReconciliationSweepAsync(CancellationToken.None);
            sweeps++;
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        var after = await MeasureAsync(harness);

        output.WriteLine($"running jobs: {RunningJobs}; duration: {_loadDuration}; heartbeat writes: {heartbeats}; "
            + $"reconciler candidate queries: {candidateQueries}; reconciliation sweeps: {sweeps}");
        output.WriteLine($"average stored record: {after.AverageRecordBytes} bytes");
        output.WriteLine($"row versions written (n_tup_upd): {after.Updates - before.Updates}; heap-only: {after.HotUpdates - before.HotUpdates}");
        output.WriteLine($"dead rows now: {after.DeadRows}; autovacuum runs during load: {after.AutovacuumRuns - before.AutovacuumRuns}");
        output.WriteLine($"heap bytes: {before.HeapBytes} -> {after.HeapBytes} ({after.HeapBytes - before.HeapBytes:+#;-#;0})");
        output.WriteLine($"TOAST bytes: {before.ToastBytes} -> {after.ToastBytes} ({after.ToastBytes - before.ToastBytes:+#;-#;0})");
        output.WriteLine($"index bytes: {before.IndexBytes} -> {after.IndexBytes} ({after.IndexBytes - before.IndexBytes:+#;-#;0})");

        (after.Updates - before.Updates).Should().BeGreaterThanOrEqualTo(heartbeats);
        (await harness.JobStore.ListActiveAsync()).Should().HaveCount(RunningJobs, "no live job may be expired by the sweeps");
    }

    [RequiredEnvironmentFact(EvidenceSwitch)]
    [Trait("Tier", "Slow")]
    public async Task QueryPlans_OnASeededTable_ArePrinted()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresJobStoreEvidenceTests));
        await SeedJobsInEveryStatusAsync(harness);
        await PrintSeededRowCountsAsync(harness);

        await ExplainClaimAsync(harness);
        await ExplainListQueriesAsync(harness);
        await ExplainReconcilerAndQueueDepthQueriesAsync(harness);
        await ExplainRetentionSweepAsync(harness);
    }

    private static async Task<List<string>> StartRunningJobsAsync(PostgresJobSubstrateHarness harness)
    {
        var operationIds = new List<string>(RunningJobs);
        for (var index = 0; index < RunningJobs; index++)
        {
            var job = CreateJobWithFourKilobyteRecord($"heartbeat-load-{index:D3}-{Guid.NewGuid():N}");
            (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
            await harness.Queue.EnqueueAsync(job.OperationId);
            operationIds.Add(job.OperationId);
        }

        for (var index = 0; index < RunningJobs; index++)
        {
            var claimedId = (await harness.Queue.TryClaimAsync($"worker-{index:D3}"))!;
            var claimed = (await harness.JobStore.GetAsync(claimedId))!;
            (await harness.JobStore.TrySetAsync(claimed with { Status = ExecutionJobStatus.Running, CurrentPhase = "Running" }))
                .Should().BeTrue();
        }

        return operationIds;
    }

    private static async Task SeedJobsInEveryStatusAsync(PostgresJobSubstrateHarness harness)
    {
        var jobs = harness.JobsTable;
        await harness.ExecuteAsync(
            $$"""
            INSERT INTO {{jobs}} (operation_id, version, status, attempt_count, claimed_by, claimed_at, last_heartbeat_at,
                updated_at, queue_priority, queue_visible_at, expires_at, record, kind, runtime_profile, backend, priority,
                created_at, cancellation_requested, heartbeat_timeout, max_duration, lookup_keys)
            SELECT 'job-' || lpad(i::text, 7, '0'), 1, s.status, CASE WHEN s.status = 'Queued' THEN 0 ELSE 1 END,
                CASE WHEN s.status IN ('Provisioning', 'Running') THEN 'worker-' || (i % 8) END,
                CASE WHEN s.status IN ('Provisioning', 'Running') THEN now() END,
                CASE WHEN s.status IN ('Provisioning', 'Running') THEN now() END,
                now(),
                CASE WHEN s.status = 'Queued' AND b.backend = 'local' THEN (i % 4)::smallint END,
                CASE WHEN s.status = 'Queued' AND b.backend = 'local'
                     THEN now() - interval '1 hour' + (i || ' milliseconds')::interval
                          + CASE WHEN i % 7 = 0 THEN interval '2 hours' ELSE interval '0' END END,
                now() + interval '7 days',
                json_build_object('operationId', 'job-' || i, 'padding', repeat(md5(i::text), 100)),
                (ARRAY['Geoprocessing', 'FileImport', 'TileExport'])[1 + i % 3],
                CASE WHEN i % 5 = 0 THEN 'native' ELSE 'managed' END,
                b.backend, 2, now() - (i || ' seconds')::interval, FALSE, interval '90 seconds', interval '1 hour',
                ARRAY['backend:' || upper(b.backend), 'requested-by:USER-' || (i % 500), 'correlation:CORR-' || i,
                      'definition:PLAN-' || (i % 2000), 'run:WF-' || (i % 4000)]
            FROM generate_series(1, 200000) AS i
            CROSS JOIN LATERAL (SELECT CASE WHEN i % 1000 < 2 THEN 'Running' WHEN i % 1000 < 3 THEN 'Provisioning'
                                            WHEN i % 1000 < 60 THEN 'Queued' WHEN i % 1000 < 90 THEN 'Failed'
                                            WHEN i % 1000 < 100 THEN 'Cancelled' ELSE 'Succeeded' END AS status) AS s
            CROSS JOIN LATERAL (SELECT CASE WHEN i % 50 = 0 THEN 'aws-batch' ELSE 'local' END AS backend) AS b
            """);
        await harness.ExecuteAsync($"VACUUM ANALYZE {jobs}");
    }

    private async Task PrintSeededRowCountsAsync(PostgresJobSubstrateHarness harness)
    {
        var jobs = harness.JobsTable;
        var counts = new StringBuilder("seeded rows by status:");
        await using (var command = harness.DataSource.CreateCommand($"SELECT status, count(*) FROM {jobs} GROUP BY status ORDER BY status"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                counts.Append(CultureInfo.InvariantCulture, $" {reader.GetString(0)}={reader.GetInt64(1)}");
            }
        }

        output.WriteLine(counts.ToString());
    }

    private async Task ExplainClaimAsync(PostgresJobSubstrateHarness harness)
    {
        var jobs = harness.JobsTable;
        await ExplainAsync(harness, "claim, one runtime profile (rolled back)",
            $"""
            WITH next_job AS (
                SELECT operation_id FROM {jobs}
                WHERE status = 'Queued' AND queue_visible_at <= now() AND runtime_profile = 'native'
                  AND (ARRAY['Geoprocessing', 'FileImport']::text[] IS NULL OR kind = ANY (ARRAY['Geoprocessing', 'FileImport']))
                ORDER BY queue_priority, queue_visible_at, operation_id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            UPDATE {jobs} AS job
            SET status = 'Provisioning', version = job.version + 1, attempt_count = job.attempt_count + 1,
                claimed_by = 'worker-1', claimed_at = now(), last_heartbeat_at = now(), next_retry_at = NULL,
                current_phase = 'Claimed', updated_at = now()
            FROM next_job WHERE job.operation_id = next_job.operation_id
            RETURNING job.operation_id
            """,
            rollBack: true);
    }

    private async Task ExplainListQueriesAsync(PostgresJobSubstrateHarness harness)
    {
        var jobs = harness.JobsTable;
        await ExplainAsync(harness, "list, no filter, second page",
            $"SELECT record FROM {jobs} WHERE (created_at, operation_id) < (now() - interval '1 hour', 'job-0003600') ORDER BY created_at DESC, operation_id DESC LIMIT 51");
        await ExplainAsync(harness, "list by status Failed",
            $"SELECT record FROM {jobs} WHERE status = ANY (ARRAY['Failed']) ORDER BY created_at DESC, operation_id DESC LIMIT 51");
        await ExplainAsync(harness, "list by kind",
            $"SELECT record FROM {jobs} WHERE kind = 'FileImport' ORDER BY created_at DESC, operation_id DESC LIMIT 51");
        await ExplainAsync(harness, "list by requester",
            $"SELECT record FROM {jobs} WHERE lookup_keys @> ARRAY['requested-by:USER-42'] ORDER BY created_at DESC, operation_id DESC LIMIT 51");
        await ExplainAsync(harness, "list by backend (not selective)",
            $"SELECT record FROM {jobs} WHERE lookup_keys @> ARRAY['backend:LOCAL'] ORDER BY created_at DESC, operation_id DESC LIMIT 51");
        await ExplainAsync(harness, "list by correlation id",
            $"SELECT record FROM {jobs} WHERE lookup_keys @> ARRAY['correlation:CORR-777'] ORDER BY created_at DESC, operation_id DESC LIMIT 51");
        await ExplainAsync(harness, "all jobs of one orchestration run",
            $"SELECT record FROM {jobs} WHERE lookup_keys @> ARRAY['run:WF-77'] ORDER BY created_at DESC, operation_id DESC LIMIT 201");
    }

    private async Task ExplainReconcilerAndQueueDepthQueriesAsync(PostgresJobSubstrateHarness harness)
    {
        var jobs = harness.JobsTable;
        await ExplainAsync(harness, "active jobs",
            $"SELECT record FROM {jobs} WHERE status IN ('Queued', 'Provisioning', 'Running') ORDER BY updated_at DESC LIMIT 100");
        await ExplainAsync(harness, "30 s sweep candidates",
            $"""
            SELECT record FROM {jobs}
            WHERE (status IN ('Provisioning', 'Running')
                   AND (COALESCE(last_heartbeat_at, claimed_at) <= now() - heartbeat_timeout OR claimed_at <= now() - max_duration))
               OR (status = 'Queued' AND queue_visible_at IS NULL AND backend = 'local')
            """);
        await ExplainAsync(harness, "5 s backend reconciler candidates",
            $"""
            SELECT operation_id FROM {jobs}
            WHERE status IN ('Queued', 'Provisioning', 'Running')
              AND NOT (status = 'Queued' AND backend = 'local' AND attempt_count = 0 AND NOT cancellation_requested)
            """);
        await ExplainAsync(harness, "queue depth",
            $"SELECT count(*) FROM {jobs} WHERE status = 'Queued' AND queue_visible_at IS NOT NULL");
    }

    private async Task ExplainRetentionSweepAsync(PostgresJobSubstrateHarness harness)
    {
        var jobs = harness.JobsTable;
        await ExplainAsync(harness, "retention sweep batch (rolled back)",
            $"""
            WITH expired AS (
                SELECT operation_id FROM {jobs}
                WHERE status IN ('Succeeded', 'Failed', 'Cancelled') AND expires_at <= now() + interval '8 days'
                ORDER BY expires_at LIMIT 1000 FOR UPDATE SKIP LOCKED)
            DELETE FROM {jobs} AS job USING expired WHERE job.operation_id = expired.operation_id
            """,
            rollBack: true);
    }

    private async Task ExplainAsync(PostgresJobSubstrateHarness harness, string title, string statement, bool rollBack = false)
    {
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var plan = new StringBuilder();
        await using (var command = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS, COSTS OFF, TIMING OFF) {statement}", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        if (rollBack)
        {
            await transaction.RollbackAsync();
        }
        else
        {
            await transaction.CommitAsync();
        }

        output.WriteLine($"== {title}");
        output.WriteLine(plan.ToString().Replace($"\"{harness.Schema}\".", string.Empty, StringComparison.Ordinal));
        plan.ToString().Should().Contain("Execution Time");
    }

    private static async Task<TableMeasurement> MeasureAsync(PostgresJobSubstrateHarness harness)
    {
        await using var command = harness.DataSource.CreateCommand(
            $"""
            SELECT pg_stat_get_tuples_updated(job_table.oid), pg_stat_get_tuples_hot_updated(job_table.oid),
                   pg_stat_get_dead_tuples(job_table.oid), pg_stat_get_autovacuum_count(job_table.oid),
                   pg_relation_size(job_table.oid), COALESCE(pg_total_relation_size(NULLIF(job_table.reltoastrelid, 0)), 0),
                   pg_indexes_size(job_table.oid),
                   (SELECT COALESCE(avg(octet_length(record::text)), 0)::bigint FROM {harness.JobsTable})
            FROM pg_class AS job_table WHERE job_table.oid = '{harness.JobsTable}'::regclass
            """);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new TableMeasurement(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private static ExecutionJobRecord CreateJobWithFourKilobyteRecord(string operationId)
    {
        var now = DateTimeOffset.UtcNow;
        var parameters = Enumerable.Range(0, 48).ToDictionary(
            index => $"honua.geoprocessing.step.{index}.input",
            _ => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            StringComparer.Ordinal);
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "postgres-job-store-evidence",
                Parameters = parameters
            }
        };
    }

    private sealed record TableMeasurement(
        long Updates,
        long HotUpdates,
        long DeadRows,
        long AutovacuumRuns,
        long HeapBytes,
        long ToastBytes,
        long IndexBytes,
        long AverageRecordBytes);
}
