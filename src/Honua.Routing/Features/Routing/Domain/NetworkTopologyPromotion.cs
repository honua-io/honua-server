// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public enum NetworkTopologyPromotionKind
{
    Promote,

    Rollback,
}

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
    DateTimeOffset PromotedAt,
    string? RequestHash = null);

public enum NetworkTopologyPromotionRejection
{
    ActiveGenerationNotFound,

    ActiveGenerationChanged,

    StaleActiveGeneration,

    CandidateNotFound,

    CandidateNotReady,

    EvidenceUnavailable,

    RollbackTargetNotEligible,

    RollbackArtifactsMissing,

    IdempotencyKeyReused,
}

public sealed class NetworkTopologyPromotionConflictException : Exception
{
    public NetworkTopologyPromotionConflictException(NetworkTopologyPromotionRejection reason, string message)
        : base(message)
        => Reason = reason;

    public NetworkTopologyPromotionRejection Reason { get; }
}
