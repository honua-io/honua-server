// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

public enum FeatureEditConcurrencyPolicy
{
    None,

    RequireLock,

    // A lease is binding when one exists, but taking a lease stays optional so clients that never claim one can still edit.
    HonorActiveLock,

    RequireVersionToken,

    LockOrVersionToken
}

public readonly record struct FeatureEditIntent(
    FeatureRef Feature,
    string Operation,
    LockHolder? Holder = null,
    string? ExpectedVersion = null,
    string? CurrentVersion = null);

public enum FeatureEditDecisionStatus
{
    Allowed,

    LockConflict,

    VersionConflict
}

public sealed record FeatureEditDecision(
    FeatureEditDecisionStatus Status,
    FeatureEditConflictResponse? Conflict = null)
{
    public bool IsAllowed => Status == FeatureEditDecisionStatus.Allowed;

    public static FeatureEditDecision Allow { get; } = new(FeatureEditDecisionStatus.Allowed);
}

public interface IFeatureEditGuard
{
    ValueTask<FeatureEditDecision> EvaluateAsync(
        FeatureEditIntent intent,
        FeatureEditConcurrencyPolicy policy,
        CancellationToken cancellationToken = default);
}
