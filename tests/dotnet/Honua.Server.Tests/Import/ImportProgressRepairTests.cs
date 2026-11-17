// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Infrastructure.Progress;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Import;

// A worker can claim an import and end its job as cancelled before the import executor runs, so no
// import code ends the queued snapshot; operation-level reads project the durable cancellation.
public sealed class ImportProgressRepairTests
{
    [UnitTest]
    public async Task QueuedImport_WithCancelledJob_ReadsCancelled()
    {
        var queued = GeoServerImportProgress.CreateInitial("claimed-cancel", "https://example.com/geoserver/rest", "https://honua.example.com");
        var completedAt = DateTimeOffset.UtcNow;

        var repaired = await ReaderFor(Job(queued.JobId, ExecutionJobStatus.Cancelled, completedAt))
            .RepairAsync(queued, CancellationToken.None);

        repaired.Status.Should().Be(OperationStatus.Cancelled);
        repaired.CompletedAt.Should().Be(completedAt);
    }

    [UnitTest]
    public async Task QueuedImport_WithRunningJob_IsUnchanged()
    {
        var queued = GeoServerImportProgress.CreateInitial("claimed-running", "https://example.com/geoserver/rest", "https://honua.example.com");

        var repaired = await ReaderFor(Job(queued.JobId, ExecutionJobStatus.Running, null))
            .RepairAsync(queued, CancellationToken.None);

        repaired.Should().BeSameAs(queued);
    }

    private static ExecutionJobProgressReader ReaderFor(ExecutionJobRecord job)
    {
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        return new ExecutionJobProgressReader(Substitute.For<IUniversalProgressStore>(), jobs);
    }

    private static ExecutionJobRecord Job(string operationId, ExecutionJobStatus status, DateTimeOffset? completedAt) => new()
    {
        OperationId = operationId,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CompletedAt = completedAt,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.GeoServerImport,
            Backend = "local",
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            WorkloadName = "geoserver-import"
        }
    };
}
