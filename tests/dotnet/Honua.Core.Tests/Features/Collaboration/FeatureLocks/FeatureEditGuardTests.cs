// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using TestOperations = Honua.TestKit.Constants.Operations;

namespace Honua.Core.Tests.Features.Collaboration.FeatureLocks;

[Protocol(ProtocolNames.TestQuality)]
public sealed class FeatureEditGuardTests
{
    private static readonly FeatureRef Feature = new("parcels", 7, "42");
    private static readonly LockHolder Alice = new("alice", "Alice Editor", "session-a", "tenant-1");
    private static readonly LockHolder Bob = new("bob", "Bob Editor", "session-b", "tenant-1");
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_NonePolicy_AlwaysAllows()
    {
        var (guard, _) = CreateGuard();
        var intent = new FeatureEditIntent(Feature, "update");

        var decision = await guard.EvaluateAsync(intent, FeatureEditConcurrencyPolicy.None);

        decision.IsAllowed.Should().BeTrue();
        decision.Status.Should().Be(FeatureEditDecisionStatus.Allowed);
        decision.Conflict.Should().BeNull();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_RequireLock_HolderOwnsLease_Allows()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice),
            FeatureEditConcurrencyPolicy.RequireLock);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_RequireLock_HeldByOtherEditor_RejectsWithHolderMetadata()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "delete", Bob),
            FeatureEditConcurrencyPolicy.RequireLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.LockConflict);
        decision.Conflict.Should().NotBeNull();
        decision.Conflict!.Code.Should().Be(FeatureEditConflictResponse.ConflictCode);
        decision.Conflict.Reason.Should().Be("feature-lock-held");
        decision.Conflict.Operation.Should().Be("delete");
        decision.Conflict.Feature.Should().Be(Feature);
        decision.Conflict.Lock.Should().NotBeNull();
        decision.Conflict.Lock!.Holder.Should().Be(Alice);
        decision.Conflict.Version.Should().BeNull();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_RequireLock_NoLeaseHeld_RejectsWithLockRequired()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice),
            FeatureEditConcurrencyPolicy.RequireLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.LockConflict);
        decision.Conflict!.Reason.Should().Be("feature-lock-required");
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_RequireVersionToken_MatchingVersion_Allows()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", ExpectedVersion: "v5", CurrentVersion: "v5"),
            FeatureEditConcurrencyPolicy.RequireVersionToken);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_RequireVersionToken_StaleVersion_RejectsWithVersionMetadata()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "replace", ExpectedVersion: "v4", CurrentVersion: "v6"),
            FeatureEditConcurrencyPolicy.RequireVersionToken);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.VersionConflict);
        decision.Conflict!.Reason.Should().Be(FeatureVersionConflictError.ConflictCode);
        decision.Conflict.Version.Should().NotBeNull();
        decision.Conflict.Version!.ExpectedVersion.Should().Be("v4");
        decision.Conflict.Version.CurrentVersion.Should().Be("v6");
        decision.Conflict.Lock.Should().BeNull();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_RequireVersionToken_ProviderHasNoVersion_FailsClosed()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", ExpectedVersion: "v4", CurrentVersion: null),
            FeatureEditConcurrencyPolicy.RequireVersionToken);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.VersionConflict);
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_LockOrVersion_LockHeldByCaller_AllowsWithoutVersion()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice),
            FeatureEditConcurrencyPolicy.LockOrVersionToken);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_LockOrVersion_NoLockButMatchingVersion_Allows()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice, ExpectedVersion: "v9", CurrentVersion: "v9"),
            FeatureEditConcurrencyPolicy.LockOrVersionToken);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_LockOrVersion_OtherHolderAndStaleVersion_PrefersLockConflict()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Bob, ExpectedVersion: "v1", CurrentVersion: "v2"),
            FeatureEditConcurrencyPolicy.LockOrVersionToken);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.LockConflict);
        decision.Conflict!.Lock!.Holder.Should().Be(Alice);
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_LockOrVersion_NoLockAndStaleVersion_FallsBackToVersionConflict()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice, ExpectedVersion: "v1", CurrentVersion: "v2"),
            FeatureEditConcurrencyPolicy.LockOrVersionToken);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.VersionConflict);
        decision.Conflict!.Version!.CurrentVersion.Should().Be("v2");
    }

    [UnitTest]
    [Operation(TestOperations.Security)]
    public async Task EvaluateAsync_RequireLock_SameUserDifferentSession_Rejects()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        var aliceOtherSession = Alice with { SessionId = "session-a-other" };

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", aliceOtherSession),
            FeatureEditConcurrencyPolicy.RequireLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Conflict!.Lock!.Holder.Should().Be(Alice);
    }

    private static (FeatureEditGuard Guard, IFeatureLockService Locks) CreateGuard()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero));
        var locks = new InMemoryFeatureLockService(clock);
        var guard = new FeatureEditGuard(locks, clock);
        return (guard, locks);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
