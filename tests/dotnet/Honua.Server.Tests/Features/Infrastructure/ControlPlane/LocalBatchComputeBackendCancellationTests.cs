// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.ControlPlane;
using Honua.Server.Features.PrintingTools;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Local cancel must reach the execution-job notifier even when a later
/// <see cref="IJobCancellationNotifier"/> registration would win a single constructor parameter.
/// </summary>
public sealed class LocalBatchComputeBackendCancellationTests
{
    [UnitTheory]
    [InlineData(0, ExecutionJobStatus.Cancelled)]
    [InlineData(1, ExecutionJobStatus.Queued)]
    public async Task CancelAsync_AttemptedImport_LeavesReceiptRecoveryQueued(int attempts, ExecutionJobStatus expected)
    {
        var backend = new LocalBatchComputeBackend(Substitute.For<IUniversalProgressStore>(), []);
        var job = new ExecutionJobRecord
        {
            OperationId = "import-recovery",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            AttemptCount = attempts,
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.GeoservicesImport,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "import"
            }
        };

        var observation = await backend.CancelAsync(job);

        Assert.Equal(expected, observation.Status);
    }

    [UnitTheory]
    [InlineData(ExecutionJobKind.Export, OperationStatus.Completed)]
    [InlineData(ExecutionJobKind.Export, OperationStatus.Cancelled)]
    [InlineData(ExecutionJobKind.Export, OperationStatus.Failed)]
    [InlineData(ExecutionJobKind.TileCache, OperationStatus.Completed)]
    [InlineData(ExecutionJobKind.TileCache, OperationStatus.Cancelled)]
    [InlineData(ExecutionJobKind.TileCache, OperationStatus.Failed)]
    [InlineData(ExecutionJobKind.GeoservicesImport, OperationStatus.Completed)]
    [InlineData(ExecutionJobKind.GeoservicesImport, OperationStatus.Cancelled)]
    [InlineData(ExecutionJobKind.GeoservicesImport, OperationStatus.Failed)]
    [InlineData(ExecutionJobKind.GeoServerImport, OperationStatus.Completed)]
    [InlineData(ExecutionJobKind.GeoServerImport, OperationStatus.Cancelled)]
    [InlineData(ExecutionJobKind.GeoServerImport, OperationStatus.Failed)]
    public async Task ObserveAsync_ProgressCannotFinalizeDurableWorkerAttempt(ExecutionJobKind kind, OperationStatus status)
    {
        var progressStore = Substitute.For<IUniversalProgressStore>();
        progressStore.GetProgressAsync("export-race", Arg.Any<CancellationToken>())
            .Returns(Honua.Infrastructure.Progress.ExportProgress.CreateInitial("export-race", "csv", "svc", 0, 1)
                with
            { Status = status });
        var backend = new LocalBatchComputeBackend(progressStore, []);
        var job = new ExecutionJobRecord
        {
            OperationId = "export-race",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CancellationRequestedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "export"
            }
        };

        var observation = await backend.ObserveAsync(job);

        Assert.Equal(ExecutionJobStatus.Running, observation.Status);
    }

    [UnitTheory]
    [InlineData("previous")]
    [InlineData("successor")]
    public async Task CancelAsync_StaleTileClaim_DoesNotCancelSuccessor(string previousWorker)
    {
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var tokens = new ExecutionJobCancellationTokens();
        var backend = new LocalBatchComputeBackend(progressStore, [tokens]);
        using var successor = tokens.CreateLinkedTokenSource("tile-retry", ("successor", 2));
        var now = DateTimeOffset.UtcNow;
        var oldClaim = new ExecutionJobRecord
        {
            OperationId = "tile-retry",
            Status = ExecutionJobStatus.Running,
            ClaimedBy = previousWorker,
            AttemptCount = 1,
            ClaimedAt = now.AddMinutes(-1),
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.TileCache,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "tile-cache"
            }
        };

        await backend.CancelAsync(oldClaim);

        Assert.False(successor.IsCancellationRequested);
    }

    [UnitTest]
    public async Task CancelAsync_SignalsExecutionJobNotifierWhenExportRegistersLast()
    {
        var progressStore = Substitute.For<IUniversalProgressStore>();
        progressStore.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOperationProgress?>(null));

        var executionTokens = new ExecutionJobCancellationTokens();
        var printTokens = new PrintJobCancellationTokens();
        var exportNotifier = Substitute.For<IJobCancellationNotifier>();

        // Registration order matches production: execution, print, then export last.
        await using var services = new ServiceCollection()
            .AddSingleton(progressStore)
            .AddSingleton(executionTokens)
            .AddSingleton<IJobCancellationNotifier>(executionTokens)
            .AddSingleton(printTokens)
            .AddSingleton<IJobCancellationNotifier>(printTokens)
            .AddSingleton(exportNotifier)
            .AddSingleton<LocalBatchComputeBackend>()
            .BuildServiceProvider();

        var backend = services.GetRequiredService<LocalBatchComputeBackend>();
        using var cts = executionTokens.CreateLinkedTokenSource("job-x", ("worker-1", 1));
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "job-x",
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-1",
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "test"
            }
        };

        await backend.CancelAsync(job);

        Assert.True(cts.IsCancellationRequested);
        exportNotifier.Received(1).Cancel("job-x");
    }
}
