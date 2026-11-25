// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.Features.Infrastructure.Coordination;

public sealed class LeaseHolderTests
{
    [UnitTest]
    public async Task RenewOnlyAsync_WhenLeaseExpiredAndReacquirable_FiresLeaseLostTokenWithoutReacquiring()
    {
        var store = Substitute.For<ILeaseStore>();
        store.TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.RenewAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        using var holder = new LeaseHolder(store, "test-lease", TimeSpan.FromSeconds(10));
        (await holder.TryAcquireAsync()).Should().BeTrue();
        var lostToken = holder.LeaseLostToken;
        lostToken.IsCancellationRequested.Should().BeFalse();

        (await holder.RenewOnlyAsync()).Should().BeFalse();

        holder.HasLease.Should().BeFalse();
        lostToken.IsCancellationRequested.Should().BeTrue();
        await store.Received(1).TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await store.Received(1).RenewAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryAcquireOrExtendAsync_WhenAlreadyHeldAndRenewFails_DoesNotSilentlyReacquire()
    {
        var store = Substitute.For<ILeaseStore>();
        store.TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.RenewAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        using var holder = new LeaseHolder(store, "test-lease", TimeSpan.FromSeconds(10));
        (await holder.TryAcquireOrExtendAsync()).Should().BeTrue();
        var lostToken = holder.LeaseLostToken;

        // Even though TryAcquireAsync would return true, extending a held lease must fail closed.
        (await holder.TryAcquireOrExtendAsync()).Should().BeFalse();

        holder.HasLease.Should().BeFalse();
        lostToken.IsCancellationRequested.Should().BeTrue();
        await store.Received(1).TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task MaintainAsync_WhenRenewReturnsFalse_MarksLostAndExitsWithoutReacquiring()
    {
        var store = Substitute.For<ILeaseStore>();
        store.TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.RenewAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        using var holder = new LeaseHolder(store, "test-lease", TimeSpan.FromSeconds(10));
        (await holder.TryAcquireAsync()).Should().BeTrue();
        var lostToken = holder.LeaseLostToken;

        await holder.MaintainAsync(TimeSpan.FromMilliseconds(15), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        holder.HasLease.Should().BeFalse();
        lostToken.IsCancellationRequested.Should().BeTrue();
        await store.Received(1).TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ReleaseAndDispose_ConcurrentWithLossCallback_DoesNotThrowObjectDisposedException()
    {
        var store = Substitute.For<ILeaseStore>();
        store.TryAcquireAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        store.RenewAsync(Arg.Any<LeaseClaim>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        using var holder = new LeaseHolder(store, "test-lease", TimeSpan.FromSeconds(10));
        (await holder.TryAcquireAsync()).Should().BeTrue();

        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowCallbackExit = new ManualResetEventSlim(false);
        using var registration = holder.LeaseLostToken.Register(() =>
        {
            callbackEntered.TrySetResult();
            allowCallbackExit.Wait(TimeSpan.FromSeconds(5));
        });

        var renewTask = Task.Run(() => holder.RenewOnlyAsync());
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose while MarkLost is actively firing callbacks on the token source.
        var disposeAction = () => holder.Dispose();
        disposeAction.Should().NotThrow();

        allowCallbackExit.Set();
        (await renewTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
    }
}
