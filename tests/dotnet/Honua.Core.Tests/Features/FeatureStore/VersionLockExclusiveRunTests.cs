// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class VersionLockExclusiveRunTests
{
    private static readonly VersionLockRequest _request = new("honua.versioning", Guid.NewGuid(), TimeSpan.FromMinutes(15));

    [UnitTest]
    public async Task RunExclusiveAsync_LockLostWhileWorking_CancelsTheWorkAndReportsTheVersionLocked()
    {
        var versionLock = new ControllableVersionLock();
        var workObservedCancellation = false;

        var run = versionLock.RunExclusiveAsync(_request, async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                workObservedCancellation = true;
                throw;
            }

            return 0;
        });
        await versionLock.Handle.Acquired.Task;
        versionLock.Handle.LoseLock();

        var act = () => run;
        (await act.Should().ThrowAsync<VersionLockedException>()).Which.VersionId.Should().Be(_request.VersionId);
        workObservedCancellation.Should().BeTrue();
        versionLock.Handle.Disposed.Should().BeTrue();
    }

    [UnitTest]
    public async Task RunExclusiveAsync_CallerCancels_PropagatesTheCancellation()
    {
        var versionLock = new ControllableVersionLock();
        using var caller = new CancellationTokenSource();

        var run = versionLock.RunExclusiveAsync(
            _request,
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            caller.Token);
        await versionLock.Handle.Acquired.Task;
        await caller.CancelAsync();

        var act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();
        versionLock.Handle.Disposed.Should().BeTrue();
    }

    [UnitTest]
    public async Task RunExclusiveAsync_LockHeldElsewhere_ReportsTheVersionLockedWithoutRunningTheWork()
    {
        var versionLock = new ControllableVersionLock { Contended = true };
        var ran = false;

        var act = () => versionLock.RunExclusiveAsync(_request, _ =>
        {
            ran = true;
            return Task.FromResult(0);
        });

        await act.Should().ThrowAsync<VersionLockedException>();
        ran.Should().BeFalse();
    }

    [UnitTest]
    public async Task RunExclusiveAsync_WorkCompletes_ReturnsItsResultAndReleasesTheLock()
    {
        var versionLock = new ControllableVersionLock();

        var result = await versionLock.RunExclusiveAsync(_request, _ => Task.FromResult(42));

        result.Should().Be(42);
        versionLock.Handle.Disposed.Should().BeTrue();
    }

    private sealed class ControllableVersionLock : IVersionLock
    {
        public bool Contended { get; init; }

        public ControllableHandle Handle { get; } = new();

        public Task<IVersionLockHandle?> TryAcquireAsync(
            string service,
            Guid versionId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            if (Contended)
            {
                return Task.FromResult<IVersionLockHandle?>(null);
            }

            Handle.Acquired.TrySetResult();
            return Task.FromResult<IVersionLockHandle?>(Handle);
        }
    }

    private sealed class ControllableHandle : IVersionLockHandle
    {
        private readonly CancellationTokenSource _lost = new();

        public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public CancellationToken LockLostToken => _lost.Token;

        public void LoseLock() => _lost.Cancel();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
