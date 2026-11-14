// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Export;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Export)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresExportRetryCapacityTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task RetryAndNewSubmissionAcrossNodes_ShareTheSameCapacityLimit()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresExportRetryCapacityTests));
        var failed = CreateJob("failed", ExecutionJobStatus.Failed);
        (await harness.JobStore.TryCreateAsync(failed)).Should().BeTrue();
        failed = (await harness.JobStore.GetAsync(failed.OperationId))!;
        var retry = failed with { Status = ExecutionJobStatus.Queued };

        var outcomes = await Task.WhenAll(
            harness.JobStore.TryRetryWithinCapacityAsync(retry, 1),
            harness.CreateNodeJobStore().TryCreateWithinCapacityAsync(CreateJob("new", ExecutionJobStatus.Queued), 1));

        outcomes.Count(admitted => admitted).Should().Be(1);
        (await harness.JobStore.GetAsync("failed"))!.Status.Should().Be(
            outcomes[0] ? ExecutionJobStatus.Queued : ExecutionJobStatus.Failed);
        (await harness.JobStore.TryRetryWithinCapacityAsync(retry, 1)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Retry_RequiresTheSameTerminalVersionAndPreservesRejectedRecords()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresExportRetryCapacityTests));
        await harness.JobStore.TryCreateAsync(CreateJob("failed", ExecutionJobStatus.Failed));
        var failed = (await harness.JobStore.GetAsync("failed"))!;
        var retry = failed with { Status = ExecutionJobStatus.Queued };
        (await harness.JobStore.TryRetryWithinCapacityAsync(retry with { Version = failed.Version - 1 }, 5)).Should().BeFalse();
        (await harness.JobStore.GetAsync("failed"))!.Status.Should().Be(ExecutionJobStatus.Failed);
        (await harness.JobStore.TryRetryWithinCapacityAsync(retry, 5)).Should().BeTrue();
        (await harness.JobStore.TryRetryWithinCapacityAsync(retry, 5)).Should().BeFalse();
        var queued = (await harness.JobStore.GetAsync("failed"))!;
        queued.Status.Should().Be(ExecutionJobStatus.Queued);
        queued.Version.Should().Be(failed.Version + 1);
        (await harness.JobStore.TryRetryWithinCapacityAsync(queued, 5)).Should().BeFalse();
    }

    private static ExecutionJobRecord CreateJob(string operationId, ExecutionJobStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Export,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "export"
            }
        };
    }
}
