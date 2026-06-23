// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

/// <summary>
/// Default <see cref="IFeatureEditGuard"/> implementation. Combines lock-lease
/// state from an <see cref="IFeatureLockService"/> with optimistic-concurrency
/// version comparison to produce a typed verdict for a feature edit.
/// </summary>
public sealed class FeatureEditGuard : IFeatureEditGuard
{
    private readonly IFeatureLockService _locks;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureEditGuard"/> class.
    /// </summary>
    /// <param name="locks">The lock service consulted for lock-based policies.</param>
    /// <param name="timeProvider">The time provider used to stamp conflicts, or <see langword="null"/> for the system clock.</param>
    public FeatureEditGuard(IFeatureLockService locks, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(locks);
        _locks = locks;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<FeatureEditDecision> EvaluateAsync(
        FeatureEditIntent intent,
        FeatureEditConcurrencyPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Feature.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Feature.FeatureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Operation);

        if (policy == FeatureEditConcurrencyPolicy.None)
        {
            return FeatureEditDecision.Allow;
        }

        var lockSatisfied = await IsLockSatisfiedAsync(intent, cancellationToken).ConfigureAwait(false);
        var versionSatisfied = IsVersionSatisfied(intent);

        return policy switch
        {
            FeatureEditConcurrencyPolicy.RequireLock =>
                lockSatisfied.Satisfied
                    ? FeatureEditDecision.Allow
                    : LockConflict(intent, lockSatisfied.HeldByOther),

            FeatureEditConcurrencyPolicy.RequireVersionToken =>
                versionSatisfied
                    ? FeatureEditDecision.Allow
                    : VersionConflict(intent),

            // LockOrVersionToken: allow when either control is satisfied. When
            // neither is satisfied, prefer surfacing a held-by-other lock conflict
            // (it identifies the blocking editor) over a bare version conflict.
            FeatureEditConcurrencyPolicy.LockOrVersionToken =>
                lockSatisfied.Satisfied || versionSatisfied
                    ? FeatureEditDecision.Allow
                    : lockSatisfied.HeldByOther is not null
                        ? LockConflict(intent, lockSatisfied.HeldByOther)
                        : VersionConflict(intent),

            _ => FeatureEditDecision.Allow
        };
    }

    private async ValueTask<LockEvaluation> IsLockSatisfiedAsync(
        FeatureEditIntent intent,
        CancellationToken cancellationToken)
    {
        var lease = await _locks.GetActiveLeaseAsync(intent.Feature, cancellationToken).ConfigureAwait(false);

        // No active lease: the feature is not locked, so a lock-based control is
        // not satisfied (there is nothing proving the caller owns the edit) but
        // there is also no conflicting holder to report.
        if (lease is null)
        {
            return new LockEvaluation(Satisfied: false, HeldByOther: null);
        }

        // An active lease exists. It is satisfied only when the caller holds it.
        if (intent.Holder is { } holder && IsSameHolder(lease.Holder, holder))
        {
            return new LockEvaluation(Satisfied: true, HeldByOther: null);
        }

        return new LockEvaluation(Satisfied: false, HeldByOther: lease);
    }

    private static bool IsVersionSatisfied(FeatureEditIntent intent)
    {
        // When the provider does not expose a current version token, optimistic
        // concurrency cannot be evaluated; treat it as not-satisfied so a pure
        // version policy fails closed and the LockOrVersionToken path falls back
        // to the lock decision.
        if (string.IsNullOrEmpty(intent.CurrentVersion))
        {
            return false;
        }

        return string.Equals(intent.ExpectedVersion, intent.CurrentVersion, StringComparison.Ordinal);
    }

    private FeatureEditDecision LockConflict(FeatureEditIntent intent, FeatureLockLease? heldByOther)
    {
        // A lock policy can be unsatisfied either because another editor holds the
        // lease, or because the caller holds no lease at all. When a competing
        // holder exists we report it; otherwise we synthesize a self-describing
        // "no lock held" conflict so the client knows it must claim one first.
        var lockError = heldByOther is not null
            ? FeatureLockHeldError.FromLease(heldByOther)
            : new FeatureLockHeldError(
                "feature-lock-required",
                "An active lock is required to edit this feature.",
                intent.Feature,
                intent.Holder ?? UnknownHolder,
                _timeProvider.GetUtcNow());

        return new FeatureEditDecision(
            FeatureEditDecisionStatus.LockConflict,
            FeatureEditConflictResponse.FromLockHeld(intent.Operation, lockError, _timeProvider.GetUtcNow()));
    }

    private FeatureEditDecision VersionConflict(FeatureEditIntent intent)
    {
        var versionError = FeatureVersionConflictError.Create(
            intent.Feature,
            intent.ExpectedVersion,
            intent.CurrentVersion ?? string.Empty);

        return new FeatureEditDecision(
            FeatureEditDecisionStatus.VersionConflict,
            FeatureEditConflictResponse.FromVersionConflict(intent.Operation, versionError, _timeProvider.GetUtcNow()));
    }

    private static readonly LockHolder UnknownHolder = new("(none)");

    private static bool IsSameHolder(LockHolder existing, LockHolder candidate)
    {
        if (!string.Equals(existing.HolderId, candidate.HolderId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existing.SessionId) || !string.IsNullOrWhiteSpace(candidate.SessionId))
        {
            return string.Equals(existing.SessionId, candidate.SessionId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(existing.TenantId) || !string.IsNullOrWhiteSpace(candidate.TenantId))
        {
            return string.Equals(existing.TenantId, candidate.TenantId, StringComparison.Ordinal);
        }

        return true;
    }

    private readonly record struct LockEvaluation(bool Satisfied, FeatureLockLease? HeldByOther);
}
