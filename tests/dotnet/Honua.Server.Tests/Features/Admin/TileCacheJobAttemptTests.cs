// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

using Honua.Infrastructure.Progress;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class TileCacheJobExecutorPublishTests
{
    [UnitTest]
    public async Task ExecuteAsync_RetiredAttemptCompletesAfterSuccessor_PreservesSuccessorResult()
    {
        var progress = new ComparingInMemoryProgressStore();
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = PauseTileCompletion(progress, (completing, resume));
        using var services = BuildScope(new StubCloudStorage(), includeCloudStorage: true);
        var job = ArchiveAttempt();
        var context = PublishingContext();
        var retired = CreateExecutor(paused, services).ExecuteAsync(job, context, CancellationToken.None);
        try
        {
            await completing.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var successorContext = PublishingContext();
            var result = await CreateExecutor(progress, services).ExecuteAsync(
                job with { AttemptCount = 2 }, successorContext, CancellationToken.None);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            var winner = await progress.GetProgressAsync<TileOperationProgress>(job.OperationId);
            winner!.DownloadUrl.Should().NotBeNullOrWhiteSpace();
            resume.TrySetResult();
            Func<Task> finishRetired = async () => await retired;
            await finishRetired.Should().ThrowAsync<OperationCanceledException>();
            (await progress.GetProgressAsync<TileOperationProgress>(job.OperationId)).Should().Be(winner);
            await context.DidNotReceive().TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            resume.TrySetResult();
            var cleanupError = await Record.ExceptionAsync(async () => await retired);
            if (cleanupError is not null)
            {
                cleanupError.Should().BeAssignableTo<OperationCanceledException>();
            }
        }

    }

    private static IUniversalProgressStore PauseTileCompletion(IUniversalProgressStore progress,
        (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        var paused = Substitute.For<IUniversalProgressStore>();
        paused.GetProgressAsync<TileOperationProgress>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => progress.GetProgressAsync<TileOperationProgress>(call.Arg<string>()));
        paused.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => progress.GetProgressAsync(call.Arg<string>()));
        paused.SetProgressAsync(Arg.Any<string>(), Arg.Any<IOperationProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var snapshot = call.Arg<IOperationProgress>();
                await PauseCompletionAsync(snapshot, signals);
                await progress.SetProgressAsync(call.Arg<string>(), snapshot);
            });
        paused.TryReplaceProgressAsync(Arg.Any<OperationProgressWrite>(), Arg.Any<IOperationProgress?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var write = call.Arg<OperationProgressWrite>();
                await PauseCompletionAsync(write.Progress, signals);
                return await progress.TryReplaceProgressAsync(write, call.Arg<IOperationProgress?>());
            });
        return paused;
    }

    private static async Task PauseCompletionAsync(IOperationProgress snapshot,
        (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        if (snapshot.Status == OperationStatus.Completed)
        {
            signals.Reached.TrySetResult();
            await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static ExecutionJobRecord ArchiveAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Status = ExecutionJobStatus.Running,
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = TileCacheExecutionSpecBuilder.Build(new TileOperationStartRequest
            {
                Operation = "archive",
                LayerId = 1,
                MinZoom = 0,
                MaxZoom = 0,
                MaxTiles = 1
            }, null, new TileCacheBatchOptions())
        };
        return job;
    }
}
