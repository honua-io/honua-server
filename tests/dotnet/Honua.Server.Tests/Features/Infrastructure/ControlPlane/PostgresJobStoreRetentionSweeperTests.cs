// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobStoreRetentionSweeperTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const int SmallBatchSize = 2;

    [IntegrationTest]
    public async Task SweepAsync_MoreExpiredTerminalJobsThanOneBatch_DeletesThemAllAndKeepsUnexpiredAndActiveJobs()
    {
        await using var harness = await CreateHarnessAsync();
        var expiredTerminal = new List<string>();
        for (var index = 0; index < (SmallBatchSize * 2) + 1; index++)
        {
            expiredTerminal.Add(await CreateJobAsync(harness, ExecutionJobStatus.Succeeded, expired: true));
        }

        var unexpiredTerminal = await CreateJobAsync(harness, ExecutionJobStatus.Failed, expired: false);
        var expiredButQueued = await CreateJobAsync(harness, ExecutionJobStatus.Queued, expired: true);
        var expiredButRunning = await CreateJobAsync(harness, ExecutionJobStatus.Running, expired: true);

        var removed = await CreateSweeper(harness).SweepAsync(CancellationToken.None);

        removed.Jobs.Should().Be(expiredTerminal.Count);
        foreach (var operationId in expiredTerminal)
        {
            (await harness.JobStore.GetAsync(operationId)).Should().BeNull();
        }

        (await harness.JobStore.GetAsync(unexpiredTerminal)).Should().NotBeNull();
        (await harness.JobStore.GetAsync(expiredButQueued)).Should().NotBeNull("a job that is not terminal is never swept");
        (await harness.JobStore.GetAsync(expiredButRunning)).Should().NotBeNull("a job that is not terminal is never swept");
    }

    [IntegrationTest]
    public async Task SweepAsync_ExpiredLogStreams_DeletesEntriesOfFinishedJobsAndKeepsTheLogOfARunningJob()
    {
        await using var harness = await CreateHarnessAsync();
        var finishedJob = await CreateJobAsync(harness, ExecutionJobStatus.Succeeded, expired: false);
        var runningJob = await CreateJobAsync(harness, ExecutionJobStatus.Running, expired: false);
        var jobAlreadyGone = "log-without-job-" + Guid.NewGuid().ToString("N");
        foreach (var operationId in new[] { finishedJob, runningJob, jobAlreadyGone })
        {
            await harness.LogStore.AppendOnceAsync(operationId, "attempt1:started", Entry("started"));
            await ExpireAsync(harness, "execution_job_log_streams", "operation_id", operationId);
        }

        var removed = await CreateSweeper(harness).SweepAsync(CancellationToken.None);

        removed.LogStreams.Should().Be(2);
        (await LogRowCountAsync(harness, finishedJob)).Should().Be(0, "entries and event identities go with their stream");
        (await LogRowCountAsync(harness, jobAlreadyGone)).Should().Be(0);
        (await harness.LogStore.TailAsync(runningJob, 10)).Items.Should().ContainSingle("a log never expires while its job runs");
        (await harness.JobStore.GetAsync(finishedJob)).Should().NotBeNull("the job has its own retention");
    }

    [IntegrationTest]
    public async Task SweepAsync_ExpiredLeasesAndResultPackages_DeletesOnlyTheExpiredOnes()
    {
        await using var harness = await CreateHarnessAsync();
        var packages = CreateResultPackageStore(harness);
        await packages.SetAsync("expired-package", Package("expired-package"));
        await packages.SetAsync("kept-package", Package("kept-package"));
        await ExpireAsync(harness, "geoprocessing_result_packages", "job_id", "expired-package");
        (await harness.JobStore.TryAcquireLeaseAsync("expired-lease", "owner", TimeSpan.FromMinutes(5))).Should().BeTrue();
        (await harness.JobStore.TryAcquireLeaseAsync("held-lease", "owner", TimeSpan.FromMinutes(5))).Should().BeTrue();
        await ExpireAsync(harness, "leases", "lease_key", "expired-lease");

        var removed = await CreateSweeper(harness).SweepAsync(CancellationToken.None);

        removed.ResultPackages.Should().Be(1);
        removed.Leases.Should().Be(1);
        (await packages.GetAsync("kept-package")).Should().NotBeNull();
        (await RowCountAsync(harness, "geoprocessing_result_packages", "job_id", "expired-package")).Should().Be(0);
        (await RowCountAsync(harness, "leases", "lease_key", "held-lease")).Should().Be(1);
        (await RowCountAsync(harness, "leases", "lease_key", "expired-lease")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task GetAsync_ResultPackagePastItsRetention_IsNotReturnedEvenBeforeTheSweepDeletesIt()
    {
        await using var harness = await CreateHarnessAsync();
        var packages = CreateResultPackageStore(harness);
        await packages.SetAsync("package", Package("package"));
        (await packages.GetAsync("package")).Should().NotBeNull();

        await ExpireAsync(harness, "geoprocessing_result_packages", "job_id", "package");

        (await packages.GetAsync("package")).Should().BeNull();
        (await RowCountAsync(harness, "geoprocessing_result_packages", "job_id", "package")).Should().Be(1);
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresJobStoreRetentionSweeperTests));

    private static JobStoreRetentionSweeper CreateSweeper(PostgresJobSubstrateHarness harness)
        => new(harness.Database, Options.Create(new JobStoreOptions { RetentionSweepBatchSize = SmallBatchSize }));

    private static PostgresGeoprocessingResultPackageStore CreateResultPackageStore(PostgresJobSubstrateHarness harness)
    {
        var executorOptions = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        executorOptions.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        return new PostgresGeoprocessingResultPackageStore(
            harness.Database, executorOptions, NullLogger<PostgresGeoprocessingResultPackageStore>.Instance);
    }

    private static async Task<string> CreateJobAsync(PostgresJobSubstrateHarness harness, ExecutionJobStatus status, bool expired)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = $"retention-{status}-{Guid.NewGuid():N}",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "retention-sweep"
            }
        };
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        if (expired)
        {
            await ExpireAsync(harness, ExecutionJobRow.TableName, "operation_id", job.OperationId);
        }

        return job.OperationId;
    }

    private static Task ExpireAsync(PostgresJobSubstrateHarness harness, string table, string keyColumn, string key)
        => harness.ExecuteAsync(
            $"UPDATE {harness.Database.Table(table)} SET expires_at = now() - interval '1 minute' WHERE {keyColumn} = @key",
            new NpgsqlParameter("key", key));

    private static Task<long> RowCountAsync(PostgresJobSubstrateHarness harness, string table, string keyColumn, string key)
        => harness.ScalarAsync<long>(
            $"SELECT count(*) FROM {harness.Database.Table(table)} WHERE {keyColumn} = @key",
            new NpgsqlParameter("key", key));

    private static async Task<long> LogRowCountAsync(PostgresJobSubstrateHarness harness, string operationId)
        => await RowCountAsync(harness, "execution_job_log_streams", "operation_id", operationId)
            + await RowCountAsync(harness, "execution_job_log_entries", "operation_id", operationId)
            + await RowCountAsync(harness, "execution_job_log_event_identities", "operation_id", operationId);

    private static AnalysisResultPackage Package(string id) => new()
    {
        ResultPackageId = id,
        Status = GeoprocessingWorkflowStatus.Completed,
        Summary = new ResultSummary { Title = id },
        Provenance = null!
    };

    private static ExecutionLogEntry Entry(string message) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Level = ExecutionLogLevel.Info,
        Message = message
    };
}
