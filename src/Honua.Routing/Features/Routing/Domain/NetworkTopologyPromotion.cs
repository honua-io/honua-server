// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Distinguishes a forward promotion (a freshly built <c>ready</c> candidate becoming
/// active) from a rollback (re-activating a previously active, now-<c>retired</c>
/// generation) in the immutable promotion history (#2719).
/// </summary>
public enum NetworkTopologyPromotionKind
{
    /// <summary>A <c>ready</c> candidate generation was promoted to active.</summary>
    Promote,

    /// <summary>A previously active, retired generation was re-activated.</summary>
    Rollback,
}

/// <summary>
/// One immutable entry in a dataset's active-generation promotion history (#2719).
/// </summary>
/// <param name="PromotionId">Stable promotion identifier.</param>
/// <param name="DatasetId">Stable network-dataset identifier.</param>
/// <param name="FromGeneration">The generation that was active before this promotion, if any.</param>
/// <param name="ToGeneration">The generation that became active as a result of this promotion.</param>
/// <param name="Kind">Whether this was a forward promotion or a rollback.</param>
/// <param name="Actor">Authenticated admin identity that performed the promotion.</param>
/// <param name="Reason">Optional operator-supplied reason.</param>
/// <param name="IdempotencyKey">Client-supplied at-most-once key for this promotion request.</param>
/// <param name="EvidenceDigest">Integrity-evidence digest of the generation that became active.</param>
/// <param name="PromotedAt">When this promotion committed.</param>
public sealed record NetworkTopologyPromotionRecord(
    string PromotionId,
    string DatasetId,
    long? FromGeneration,
    long ToGeneration,
    NetworkTopologyPromotionKind Kind,
    string Actor,
    string? Reason,
    string IdempotencyKey,
    string? EvidenceDigest,
    DateTimeOffset PromotedAt);

/// <summary>
/// Stable, sanitized rejection reasons for a promotion or rollback request (#2719).
/// </summary>
public enum NetworkTopologyPromotionRejection
{
    /// <summary>The dataset has no active generation to promote from.</summary>
    ActiveGenerationNotFound,

    /// <summary>
    /// The caller's expected active generation exists but is no longer in the <c>active</c>
    /// state (typically because a concurrent promotion/rollback already won the race). This
    /// is an optimistic-concurrency conflict on an existing row, not a missing resource.
    /// </summary>
    ActiveGenerationChanged,

    /// <summary>The caller's expected active generation/row version no longer matches.</summary>
    StaleActiveGeneration,

    /// <summary>The candidate generation does not exist.</summary>
    CandidateNotFound,

    /// <summary>Promotion requires a <c>ready</c> candidate; the candidate is not ready.</summary>
    CandidateNotReady,

    /// <summary>The candidate's shadow topology artifacts are missing (evidence unavailable).</summary>
    EvidenceUnavailable,

    /// <summary>Rollback requires a <c>retired</c> target generation; the target is not eligible.</summary>
    RollbackTargetNotEligible,

    /// <summary>The rollback target's physical artifacts are missing (retention-expired).</summary>
    RollbackArtifactsMissing,
}

/// <summary>
/// Thrown when a promotion or rollback request is rejected deterministically for a stable,
/// sanitized reason. Callers map <see cref="Reason"/> to HTTP 404/409.
/// </summary>
public sealed class NetworkTopologyPromotionConflictException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyPromotionConflictException"/> class.
    /// </summary>
    public NetworkTopologyPromotionConflictException(NetworkTopologyPromotionRejection reason, string message)
        : base(message)
        => Reason = reason;

    /// <summary>Gets the stable rejection reason.</summary>
    public NetworkTopologyPromotionRejection Reason { get; }
}
