// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

[Protocol(TestProtocols.Infrastructure)]
public sealed class InMemoryFeatureLockServiceTests
{
    private static readonly FeatureRef Feature = new("parcels", 7, "42");
    private static readonly LockHolder Alice = new("alice", "Alice Editor", "session-a", "tenant-1");
    private static readonly LockHolder Bob = new("bob", "Bob Editor", "session-b", "tenant-1");
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ClaimAsync_AuthorizedWriteAccess_ClaimsLease()
    {
        var clock = new MutableTimeProvider(Instant());
        var service = new InMemoryFeatureLockService(clock);

        var result = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        result.Status.Should().Be(FeatureLockClaimStatus.Claimed);
        result.IsSuccess.Should().BeTrue();
        result.Lease.Should().NotBeNull();
        result.Lease!.Feature.Should().Be(Feature);
        result.Lease.Holder.Should().Be(Alice);
        result.Lease.AcquiredAt.Should().Be(clock.GetUtcNow());
        result.Lease.ExpiresAt.Should().Be(clock.GetUtcNow().Add(LeaseDuration));
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public async Task ClaimAsync_UnauthorizedOrReadOnlyAccess_DeniesFailClosed()
    {
        var service = new InMemoryFeatureLockService(new MutableTimeProvider(Instant()));

        var unauthorized = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.Unauthorized);
        var readOnly = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.ReadOnly);

        unauthorized.Status.Should().Be(FeatureLockClaimStatus.Denied);
        unauthorized.IsSuccess.Should().BeFalse();
        readOnly.Status.Should().Be(FeatureLockClaimStatus.Denied);
        readOnly.IsSuccess.Should().BeFalse();
        (await service.GetActiveLeaseAsync(Feature)).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ClaimAsync_ActiveLeaseByDifferentHolder_ReturnsLockHeldMetadata()
    {
        var service = new InMemoryFeatureLockService(new MutableTimeProvider(Instant()));
        var first = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var second = await service.ClaimAsync(Feature, Bob, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        second.Status.Should().Be(FeatureLockClaimStatus.HeldByOther);
        second.IsSuccess.Should().BeFalse();
        second.Error.Should().NotBeNull();
        second.Error!.Code.Should().Be("feature-lock-held");
        second.Error.Feature.Should().Be(Feature);
        second.Error.Holder.Should().Be(Alice);
        second.Error.ExpiresAt.Should().Be(first.Lease!.ExpiresAt);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ClaimAsync_ActiveLeaseBySameHolder_RenewsLease()
    {
        var clock = new MutableTimeProvider(Instant());
        var service = new InMemoryFeatureLockService(clock);
        var first = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        clock.Advance(TimeSpan.FromSeconds(30));

        var second = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        second.Status.Should().Be(FeatureLockClaimStatus.Renewed);
        second.Lease!.LockId.Should().Be(first.Lease!.LockId);
        second.Lease.RenewedAt.Should().Be(clock.GetUtcNow());
        second.Lease.ExpiresAt.Should().Be(clock.GetUtcNow().Add(LeaseDuration));
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ClaimAsync_SameHolderDifferentSession_ReturnsLockHeld()
    {
        var service = new InMemoryFeatureLockService(new MutableTimeProvider(Instant()));
        var sameUserOtherSession = Alice with { SessionId = "session-a-other" };
        await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var result = await service.ClaimAsync(
            Feature,
            sameUserOtherSession,
            LeaseDuration,
            FeatureLockAccessContext.AuthorizedWrite);

        result.Status.Should().Be(FeatureLockClaimStatus.HeldByOther);
        result.Error!.Holder.Should().Be(Alice);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task RenewAsync_SameHolder_ExtendsLease()
    {
        var clock = new MutableTimeProvider(Instant());
        var service = new InMemoryFeatureLockService(clock);
        var claim = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        clock.Advance(TimeSpan.FromMinutes(1));

        var renew = await service.RenewAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        renew.Status.Should().Be(FeatureLockRenewStatus.Renewed);
        renew.IsSuccess.Should().BeTrue();
        renew.Lease!.LockId.Should().Be(claim.Lease!.LockId);
        renew.Lease.ExpiresAt.Should().Be(clock.GetUtcNow().Add(LeaseDuration));
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ReleaseAsync_SameHolder_ReleasesLease()
    {
        var service = new InMemoryFeatureLockService(new MutableTimeProvider(Instant()));
        var claim = await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var release = await service.ReleaseAsync(Feature, Alice, FeatureLockAccessContext.AuthorizedWrite);

        release.Status.Should().Be(FeatureLockReleaseStatus.Released);
        release.IsSuccess.Should().BeTrue();
        release.ReleasedLease.Should().Be(claim.Lease);
        (await service.GetActiveLeaseAsync(Feature)).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ClaimAsync_ExpiredLease_PrunesAndAllowsNewHolder()
    {
        var clock = new MutableTimeProvider(Instant());
        var service = new InMemoryFeatureLockService(clock);
        await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        clock.Advance(LeaseDuration.Add(TimeSpan.FromTicks(1)));

        var result = await service.ClaimAsync(Feature, Bob, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        result.Status.Should().Be(FeatureLockClaimStatus.Claimed);
        result.Lease!.Holder.Should().Be(Bob);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task PruneExpiredAsync_RemovesExpiredLeases()
    {
        var clock = new MutableTimeProvider(Instant());
        var service = new InMemoryFeatureLockService(clock);
        await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        clock.Advance(LeaseDuration.Add(TimeSpan.FromTicks(1)));

        var removed = await service.PruneExpiredAsync();

        removed.Should().Be(1);
        (await service.GetActiveLeaseAsync(Feature)).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    public async Task FeatureEditConflictResponse_FromLockHeld_HasStableShape()
    {
        var clock = new MutableTimeProvider(Instant());
        var service = new InMemoryFeatureLockService(clock);
        await service.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        var held = await service.ClaimAsync(Feature, Bob, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var conflict = FeatureEditConflictResponse.FromLockHeld("replace", held.Error!, clock.GetUtcNow());

        conflict.Code.Should().Be("edit-conflict");
        conflict.Reason.Should().Be("feature-lock-held");
        conflict.Operation.Should().Be("replace");
        conflict.Feature.Should().Be(Feature);
        conflict.Lock.Should().NotBeNull();
        conflict.Lock!.Holder.DisplayName.Should().Be("Alice Editor");

        var json = JsonSerializer.Serialize(conflict);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Code").GetString().Should().Be("edit-conflict");
        document.RootElement.GetProperty("Reason").GetString().Should().Be("feature-lock-held");
        document.RootElement.GetProperty("Lock").GetProperty("Holder").GetProperty("HolderId").GetString().Should().Be("alice");
    }

    private static DateTimeOffset Instant()
        => new(2026, 5, 11, 10, 0, 0, TimeSpan.Zero);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
        }
    }
}
