// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Coordination;

public sealed class LeaseVersionLockLossTests
{
    private static readonly TimeSpan _shortLease = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan _observationTimeout = TimeSpan.FromSeconds(10);

    [UnitTest]
    public async Task LockLostToken_RenewalFailsWithAStoreError_IsCancelled()
    {
        var leases = new ScriptedLeaseStore { RenewFailure = new InvalidOperationException("store unavailable") };
        var versionLock = new LeaseVersionLock(leases);

        await using var handle = await versionLock.TryAcquireAsync("honua.versioning", Guid.NewGuid(), _shortLease);

        handle.Should().NotBeNull();
        handle!.LockLostToken.IsCancellationRequested.Should().BeFalse();
        await WaitForCancellationAsync(handle.LockLostToken);
        handle.LockLostToken.IsCancellationRequested.Should().BeTrue();
    }

    [UnitTest]
    public async Task LockLostToken_RenewalRefusedBecauseAnotherOwnerHoldsTheLease_IsCancelled()
    {
        var leases = new ScriptedLeaseStore { RenewResult = false };
        var versionLock = new LeaseVersionLock(leases);

        await using var handle = await versionLock.TryAcquireAsync("honua.versioning", Guid.NewGuid(), _shortLease);

        await WaitForCancellationAsync(handle!.LockLostToken);
        handle.LockLostToken.IsCancellationRequested.Should().BeTrue();
    }

    [UnitTest]
    public async Task LockLostToken_WhileRenewalsSucceed_StaysUncancelled()
    {
        var leases = new ScriptedLeaseStore { RenewResult = true };
        var versionLock = new LeaseVersionLock(leases);

        await using var handle = await versionLock.TryAcquireAsync("honua.versioning", Guid.NewGuid(), _shortLease);
        await WaitUntilAsync(() => leases.RenewCount >= 3);

        handle!.LockLostToken.IsCancellationRequested.Should().BeFalse();
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        var cancelled = new TaskCompletionSource();
        await using var registration = token.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(_observationTimeout);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(_observationTimeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private sealed class ScriptedLeaseStore : ILeaseStore
    {
        private int _renewCount;

        public bool RenewResult { get; init; } = true;

        public Exception? RenewFailure { get; init; }

        public int RenewCount => Volatile.Read(ref _renewCount);

        public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renewCount);
            return RenewFailure is null ? Task.FromResult(RenewResult) : Task.FromException<bool>(RenewFailure);
        }

        public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
