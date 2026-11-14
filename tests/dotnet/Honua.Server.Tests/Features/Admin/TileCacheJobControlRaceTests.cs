// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Jobs;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class TileCacheJobExecutorPublishTests
{
    [UnitTest]
    public async Task ConsoleRetry_CancelledTileJob_ExecutesPastHistoricalCancelledSnapshot()
    {
        var job = ControlJob(ExecutionJobStatus.Cancelled) with
        { CancellationRequestedAt = DateTimeOffset.UtcNow };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(_ => job);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { job = call.Arg<ExecutionJobRecord>(); return true; });
        var progress = new ComparingInMemoryProgressStore();
        await progress.SetProgressAsync(job.OperationId,
            TileOperationProgress.CreateInitial(job.OperationId, "archive", null, 1, "WebMercatorQuad") with
            { Status = OperationStatus.Cancelled });
        using var controls = ControlServices(store);
        var console = CreateConsole(controls);

        await console.RetryAsync(new DefaultHttpContext(), job.OperationId, CancellationToken.None);

        job.Status.Should().Be(ExecutionJobStatus.Queued);
        job.CancellationRequestedAt.Should().BeNull();
        using var services = BuildScope(new StubCloudStorage(), includeCloudStorage: true);
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var result = await CreateExecutor(progress, services).ExecuteAsync(
            job with { Status = ExecutionJobStatus.Running, AttemptCount = 1 }, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        (await progress.GetProgressAsync<TileOperationProgress>(job.OperationId))!.Status
            .Should().Be(OperationStatus.Completed);
        await context.Received().ThrowIfExecutionLeaseLostAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TileCancel_CompletesAfterConsoleRetry_PreservesSuccessorProgress()
    {
        var job = ControlJob(ExecutionJobStatus.Queued);
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(_ => job);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PauseTerminalCancellation(store, candidate => job = candidate, (cancelling, resume));
        var progress = new ComparingInMemoryProgressStore();
        await progress.SetProgressAsync(job.OperationId,
            TileOperationProgress.CreateInitial(job.OperationId, "archive", null, 1, "WebMercatorQuad"));
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns(LocalBatchComputeBackend.BackendId);
        backend.TargetKind.Returns(job.Spec.TargetKind);
        using var controls = ControlServices(store, backend: backend);
        var service = CreateControlTileService(store, progress, (controls, [backend]));
        var cancel = service.CancelAsync(job.OperationId);
        TileOperationProgress? successor = null;
        try
        {
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (await service.GetAsync(job.OperationId))!.Status.Should().Be(OperationStatus.Cancelled);
            await CreateConsole(controls).RetryAsync(new DefaultHttpContext(), job.OperationId, CancellationToken.None);
            job.Status.Should().Be(ExecutionJobStatus.Queued);
            successor = TileOperationProgress.CreateInitial(job.OperationId, "archive", null, 1, "WebMercatorQuad") with
            { Status = OperationStatus.Processing, ProcessedTiles = 1 };
            await progress.SetProgressAsync(job.OperationId, successor);
        }
        finally
        {
            resume.TrySetResult();
            (await cancel).Should().BeTrue();
        }
        (await progress.GetProgressAsync<TileOperationProgress>(job.OperationId)).Should().Be(successor);
    }

    [UnitTest]
    public async Task ConsoleCancel_BackendResponseArrivesAfterRetry_DoesNotCancelSuccessor()
    {
        var original = ControlJob(ExecutionJobStatus.Running) with
        {
            ProviderOperationId = "old-provider",
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var successor = RetriedJob(original);
        var current = original;
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(original.OperationId, Arg.Any<CancellationToken>()).Returns(_ => current);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var candidate = call.Arg<ExecutionJobRecord>();
                if (candidate.Version != current.Version) return false;
                current = candidate with { Version = candidate.Version + 1 };
                return true;
            });
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns(original.Spec.Backend);
        backend.TargetKind.Returns(original.Spec.TargetKind);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            current = successor;
            return new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled };
        });
        using var controls = ControlServices(store, backend: backend);

        var cancel = () => CreateConsole(controls).CancelAsync(new DefaultHttpContext(), original.OperationId, CancellationToken.None);

        await cancel.Should().ThrowAsync<ConsoleJobConflictException>();
        current.Should().BeSameAs(successor);
        await store.Received(1).TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("console-queued")]
    [InlineData("console-running")]
    [InlineData("tile")]
    [InlineData("console-backend")]
    [InlineData("tile-remote")]
    public async Task ConcurrentCancel_OnlyTransitionWinnerReleasesOutstandingJob(string path)
    {
        var initial = ControlJob(path == "console-running" ? ExecutionJobStatus.Running : ExecutionJobStatus.Queued);
        var backend = CancellableControlBackend(initial.Spec with { Backend = "remote-test" });
        if (path is "console-backend" or "tile-remote")
        {
            initial = initial with
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = "provider-operation",
                CancellationRequestedAt = DateTimeOffset.UtcNow,
                Spec = initial.Spec with { Backend = backend.BackendName }
            };
        }
        var race = new ConcurrentCancellationStore(initial);
        var store = race.Store;
        var callback = Substitute.For<IJobTerminalCallback>();
        using var controls = ControlServices(store, callback, backend);
        using var released = new TileQueueDepthObserver(initial.OperationId);
        if (path is "tile" or "tile-remote")
        {
            var service = CreateControlTileService(store, new ComparingInMemoryProgressStore(),
                (controls, path == "tile-remote" ? [backend] : [new LocalBatchComputeBackend(new ComparingInMemoryProgressStore(), [])]));
            (await Task.WhenAll(service.CancelAsync(initial.OperationId), service.CancelAsync(initial.OperationId)))
                .Should().OnlyContain(result => result);
            released.Value.Should().Be(-1);
        }
        else
        {
            var console = CreateConsole(controls);
            await Task.WhenAll(console.CancelAsync(new DefaultHttpContext(), initial.OperationId, CancellationToken.None),
                console.CancelAsync(new DefaultHttpContext(), initial.OperationId, CancellationToken.None));
            await callback.Received(1).OnTerminalAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());
        }
        race.Current.Status.Should().Be(ExecutionJobStatus.Cancelled);
        race.Writers.Should().Be(2, "both callers must race the same nonterminal record");
    }

    private static void PauseTerminalCancellation(IExecutionJobStore store,
        Action<ExecutionJobRecord> setCurrent, (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var candidate = call.Arg<ExecutionJobRecord>();
                setCurrent(candidate);
                if (candidate.Status == ExecutionJobStatus.Cancelled)
                {
                    signals.Reached.TrySetResult();
                    await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                return true;
            });
    }

    private static IBatchComputeBackend CancellableControlBackend(ExecutionJobSpec spec)
    {
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns(spec.Backend);
        backend.TargetKind.Returns(spec.TargetKind);
        backend.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
            .Returns(new BatchComputeBackendCapabilities { SupportsCancellation = true });
        backend.CancelAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeObservation { Status = ExecutionJobStatus.Cancelled });
        return backend;
    }

    private static TileCacheJobService CreateControlTileService(IExecutionJobStore store,
        IUniversalProgressStore progress, (ServiceProvider Services, IBatchComputeBackend[] Backends) dependencies)
        => new(store, progress, dependencies.Backends, BatchOptions(),
            dependencies.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TileCacheJobService>.Instance,
            dependencies.Services.GetRequiredService<IJobQueue>());

    private static ExecutionJobRecord RetriedJob(ExecutionJobRecord original)
    {
        return original with
        {
            Status = ExecutionJobStatus.Queued,
            Version = original.Version + 1,
            ProviderOperationId = null,
            CancellationRequestedAt = null,
            NextRetryAt = DateTimeOffset.UtcNow
        };
    }

    private static ExecutionJobRecord ControlJob(ExecutionJobStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        var spec = TileCacheExecutionSpecBuilder.Build(new TileOperationStartRequest
        { Operation = "archive", LayerId = 1, MinZoom = 0, MaxZoom = 0, MaxTiles = 1 }, null, new TileCacheBatchOptions());
        // A unique metric tag keeps other concurrently running tile tests out of this assertion.
        if (status != ExecutionJobStatus.Cancelled)
        {
            spec = spec with
            { Parameters = new Dictionary<string, string>(spec.Parameters) { [TileCacheJobParameterKeys.Operation] = id } };
        }
        return new ExecutionJobRecord
        {
            OperationId = id, Status = status, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            AttemptCount = status == ExecutionJobStatus.Running ? 1 : 0, Spec = spec
        };
    }

    private static ServiceProvider ControlServices(IExecutionJobStore store, IJobTerminalCallback? callback = null,
        IBatchComputeBackend? backend = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IJobQueue>());
        if (callback != null) services.AddSingleton(callback);
        if (backend != null) services.AddSingleton(backend);
        return services.BuildServiceProvider();
    }

    private static ConsoleJobService CreateConsole(IServiceProvider services)
    {
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
        return new ConsoleJobService(services,
            new OperatorApprovalGate(authorization, approval, NullLogger<OperatorApprovalGate>.Instance),
            NullLogger<ConsoleJobService>.Instance, TimeProvider.System);
    }
}
