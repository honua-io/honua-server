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

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_HonorActiveLock_NoLeaseHeld_Allows()
    {
        // The policy every write path enforces (#4402). Claiming a lease stays optional,
        // so a caller that never touched /feature-locks must be unaffected — this is the
        // case that keeps enforcement from becoming a blanket block on all editing.
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeTrue();
        decision.Conflict.Should().BeNull();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_HonorActiveLock_UnidentifiedCallerWithNoLease_Allows()
    {
        var (guard, _) = CreateGuard();

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update"),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_HonorActiveLock_HolderOwnsLease_Allows()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Alice),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_HonorActiveLock_HeldByOtherEditor_RejectsWithHolderMetadata()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", Bob),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Status.Should().Be(FeatureEditDecisionStatus.LockConflict);
        decision.Conflict!.Reason.Should().Be("feature-lock-held");
        decision.Conflict.Lock!.Holder.Should().Be(Alice);
        decision.Conflict.Version.Should().BeNull();
    }

    [UnitTest]
    [Operation(TestOperations.Security)]
    public async Task EvaluateAsync_HonorActiveLock_UnidentifiedCallerAgainstHeldLease_Rejects()
    {
        // A caller the server cannot identify can never be the holder, so an active lease
        // must block it. Failing open here would let an anonymous write walk through the
        // one control the holder was given.
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "delete"),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Conflict!.Lock!.Holder.Should().Be(Alice);
        decision.Conflict.Operation.Should().Be("delete");
    }

    [UnitTest]
    [Operation(TestOperations.Security)]
    public async Task EvaluateAsync_HonorActiveLock_SameUserDifferentSession_Rejects()
    {
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(Feature, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);
        var aliceOtherSession = Alice with { SessionId = "session-a-other" };

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", aliceOtherSession),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Conflict!.Lock!.Holder.Should().Be(Alice);
    }

    [UnitTest]
    [Operation(TestOperations.Security)]
    public async Task EvaluateAsync_HonorActiveLock_ForgedHolderIdWithoutThePrincipal_Rejects()
    {
        // The attack the principal binding exists to stop. A conflict response hands the
        // blocked editor the holder's HolderId, DisplayName, SessionId and TenantId, so a
        // second editor knows every caller-supplied field on the lease. Replaying them must
        // not make it the owner: only the server-stamped principal can do that.
        var (guard, locks) = CreateGuard();
        var aliceSignedIn = Alice with { PrincipalName = "alice@example.test" };
        await locks.ClaimAsync(Feature, aliceSignedIn, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var impersonator = Alice with { PrincipalName = "mallory@example.test" };
        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", impersonator),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Conflict!.Lock!.Holder.PrincipalName.Should().Be("alice@example.test");
    }

    [UnitTest]
    [Operation(TestOperations.Security)]
    public async Task EvaluateAsync_HonorActiveLock_ForgedHolderIdWithNoPrincipalAtAll_Rejects()
    {
        // An anonymous or unauthenticated writer carries no principal, so it can never
        // satisfy a lease that recorded one — copying the holder id gains it nothing.
        var (guard, locks) = CreateGuard();
        await locks.ClaimAsync(
            Feature,
            Alice with { PrincipalName = "alice@example.test" },
            LeaseDuration,
            FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "delete", Alice),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_HonorActiveLock_MatchingPrincipal_Allows()
    {
        var (guard, locks) = CreateGuard();
        var aliceSignedIn = Alice with { PrincipalName = "alice@example.test" };
        await locks.ClaimAsync(Feature, aliceSignedIn, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(Feature, "update", aliceSignedIn),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public async Task EvaluateAsync_HonorActiveLock_CanonicalKeyMatchesAcrossCasingAndPadding()
    {
        // A lease claimed with the URL-visible spelling must block a write evaluated from
        // the writer's own spelling. Ordinal record equality on the raw strings would let
        // ("TEST", 0, "001") and ("test", 0, "1") name two different features, and
        // enforcement would look correct while doing nothing.
        var (guard, locks) = CreateGuard();
        var claimed = FeatureRef.Canonical("  TEST  ", 3, "007");
        await locks.ClaimAsync(claimed, Alice, LeaseDuration, FeatureLockAccessContext.AuthorizedWrite);

        var decision = await guard.EvaluateAsync(
            new FeatureEditIntent(FeatureRef.Canonical("test", 3, 7L), "update", Bob),
            FeatureEditConcurrencyPolicy.HonorActiveLock);

        decision.IsAllowed.Should().BeFalse();
        decision.Conflict!.Lock!.Holder.HolderId.Should().Be("alice");
    }

    [UnitTest]
    [Operation(TestOperations.Update)]
    public void FeatureRefCanonical_NormalizesCasingPaddingAndNonNumericIds()
    {
        FeatureRef.Canonical("TEST", 3, "007").Should().Be(new FeatureRef("test", 3, "7"));
        FeatureRef.Canonical(" Test ", 3, " 7 ").Should().Be(new FeatureRef("test", 3, "7"));
        FeatureRef.Canonical("test", 3, 7L).Should().Be(new FeatureRef("test", 3, "7"));

        // A non-numeric public identifier is trimmed but otherwise preserved verbatim:
        // there is no safe normalisation for an opaque id.
        FeatureRef.Canonical("test", 3, " PARCEL-07 ").Should().Be(new FeatureRef("test", 3, "PARCEL-07"));
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
