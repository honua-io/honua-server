// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class PostgresUniversalProgressStoreTests
{
    [IntegrationTheory]
    [InlineData("replacement-worker", 2, ExecutionJobStatus.Running)]
    [InlineData("original-worker", 2, ExecutionJobStatus.Running)]
    [InlineData("original-worker", 1, ExecutionJobStatus.Succeeded)]
    public async Task TryReplaceProgressAsync_SupersededAttempt_CannotPublishFalseFailure(
        string owner, int attempt, ExecutionJobStatus status)
    {
        await using var harness = await CreateHarnessAsync();
        var processing = Export(OperationStatus.Processing);
        var job = ProgressJob(processing.JobId) with { ClaimedBy = owner, AttemptCount = attempt, Status = status };
        await harness.CreateJobStore().SetAsync(job);
        var store = harness.CreateProgressStore();
        await store.SetProgressAsync(processing.JobId, processing);
        var staleFailure = new OperationProgressWrite(processing.JobId, processing with { Status = OperationStatus.Failed })
        {
            ExecutionFence = new ProgressExecutionFence("original-worker", 1)
        };

        var write = () => store.TryReplaceProgressAsync(staleFailure, processing);

        await write.Should().ThrowAsync<OperationCanceledException>();
        (await store.GetProgressAsync(processing.JobId))!.Status.Should().Be(OperationStatus.Processing);
    }

    [IntegrationTest]
    public async Task TryReplaceProgressAsync_CurrentAttempt_CanPublishCompletion()
    {
        await using var harness = await CreateHarnessAsync();
        var processing = Export(OperationStatus.Processing);
        await harness.CreateJobStore().SetAsync(ProgressJob(processing.JobId));
        var store = harness.CreateProgressStore();
        await store.SetProgressAsync(processing.JobId, processing);

        var result = await store.TryReplaceProgressAsync(new OperationProgressWrite(
            processing.JobId, processing with { Status = OperationStatus.Completed })
        {
            ExecutionFence = new ProgressExecutionFence("original-worker", 1)
        }, processing);

        result.Outcome.Should().Be(ProgressCompareAndSetOutcome.Updated);
        (await store.GetProgressAsync(processing.JobId))!.Status.Should().Be(OperationStatus.Completed);
    }

    private static ExecutionJobRecord ProgressJob(string operationId) => new()
    {
        OperationId = operationId,
        Status = ExecutionJobStatus.Running,
        ClaimedBy = "original-worker",
        AttemptCount = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.GeoservicesImport,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "import"
        }
    };
}
