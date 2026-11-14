// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Realtime;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Realtime;

[Collection("Unit")]
public sealed class RealtimeStreamLeaseTests
{
    [UnitTest]
    public async Task RenewFailure_TerminatesOwnershipAndCancelsPendingReads()
    {
        var store = new RenewalStore(_ => Task.FromResult(false));
        await using var lease = await RealtimeStreamLease.TryAcquireAsync(store, "session", CancellationToken.None,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        lease.Should().NotBeNull();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease!.LostToken.Register(() => lost.TrySetResult());

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lease.IsHeld.Should().BeFalse();
        store.Acquisitions.Should().Be(1, "loss must never reacquire an ownership epoch");
        await lease.DisposeAsync();
        store.Releases.Should().Be(1);
    }

    [UnitTest]
    public async Task PendingRenewal_LocalDeadlineEndsDeliveryBeforeStoreReply()
    {
        var renewal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RenewalStore(_ => renewal.Task);
        await using var lease = await RealtimeStreamLease.TryAcquireAsync(store, "session", CancellationToken.None,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(20));
        lease.Should().NotBeNull();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease!.LostToken.Register(() => lost.TrySetResult());
        try
        {
            await store.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lease.IsHeld.Should().BeFalse();
        }
        finally
        {
            renewal.TrySetResult(true);
        }

        await lease.DisposeAsync();
        lease.IsHeld.Should().BeFalse("a late successful renewal cannot revive a lost stream");
    }

    private sealed class RenewalStore(Func<CancellationToken, Task<bool>> renew) : ILeaseStore
    {
        public TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Acquisitions { get; private set; }
        public int Releases { get; private set; }

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Acquisitions++;
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Renewed.TrySetResult();
            return renew(cancellationToken);
        }

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }
}
