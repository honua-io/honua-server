// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
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
    [UnitTest]
    public async Task CancelAsync_SignalsExecutionJobNotifierWhenExportRegistersLast()
    {
        var progressStore = Substitute.For<IUniversalProgressStore>();
        progressStore.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IOperationProgress?)null);

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
        using var cts = executionTokens.CreateLinkedTokenSource("job-x", "worker-1");
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "job-x",
            Status = ExecutionJobStatus.Running,
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
