// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Wire shape returned when a shadow-topology rebuild is submitted (#2718).
/// </summary>
public sealed class NetworkTopologyRebuildSubmissionDto
{
    /// <summary>Durable execution-job id. Use <c>/api/v1/admin/jobs/{operationId}</c> for generic status/cancel/retry.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Generation being rebuilt.</summary>
    public long Generation { get; set; }

    /// <summary>Newly created rebuild attempt number.</summary>
    public long Attempt { get; set; }
}

/// <summary>
/// Wire shape for one rebuild-stage checkpoint (#2718).
/// </summary>
public sealed class NetworkTopologyRebuildCheckpointDto
{
    /// <summary>Rebuild stage: <c>snapshot</c>, <c>build</c>, <c>analyze</c>, <c>validate</c>, or <c>cleanup</c>.</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>Checkpoint status: <c>pending</c>, <c>in_progress</c>, <c>completed</c>, or <c>failed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Sanitized stage detail (e.g. edge/vertex counts). Never raw geometry or SQL.</summary>
    public string? Detail { get; set; }

    /// <summary>Last checkpoint mutation timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Wire shape for one rebuild attempt, including its stage checkpoints (#2718).
/// </summary>
public sealed class NetworkTopologyRebuildAttemptDto
{
    /// <summary>Stable network-dataset id.</summary>
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>Generation being rebuilt.</summary>
    public long Generation { get; set; }

    /// <summary>Attempt number.</summary>
    public long Attempt { get; set; }

    /// <summary>Attempt state: <c>building</c>, <c>ready</c>, or <c>failed</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Durable execution-job operation id driving this attempt.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Sanitized stable failure code, when <see cref="State"/> is <c>failed</c>.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Integrity-evidence digest, once <see cref="State"/> is <c>ready</c>.</summary>
    public string? EvidenceDigest { get; set; }

    /// <summary>Identity of the worker currently holding the rebuild lease, if any.</summary>
    public string? OwnerId { get; set; }

    /// <summary>When the current lease expires and becomes eligible for takeover.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Last time the owning worker renewed the lease.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>Attempt creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last mutation timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Terminal (ready/failed) timestamp, if reached.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Per-stage checkpoint progress.</summary>
    public NetworkTopologyRebuildCheckpointDto[] Checkpoints { get; set; } = [];
}

/// <summary>
/// Request body to promote a candidate generation or roll back to a retired one (#2719).
/// </summary>
public sealed class NetworkTopologyPromotionRequest
{
    /// <summary>Generation to promote (must be <c>ready</c>) or roll back to (must be <c>retired</c>).</summary>
    public long? TargetGeneration { get; set; }

    /// <summary>The active generation the caller last observed. Fences against a concurrent promotion.</summary>
    public long? ExpectedActiveGeneration { get; set; }

    /// <summary>The active generation's row version the caller last observed.</summary>
    public long? ExpectedActiveRowVersion { get; set; }

    /// <summary>Optional operator-supplied reason, recorded in the immutable promotion history.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Wire shape for one promotion/rollback history entry (#2719).
/// </summary>
public sealed class NetworkTopologyPromotionDto
{
    /// <summary>Stable promotion identifier.</summary>
    public string PromotionId { get; set; } = string.Empty;

    /// <summary>Stable network-dataset id.</summary>
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>The generation that was active before this promotion, if any.</summary>
    public long? FromGeneration { get; set; }

    /// <summary>The generation that became active as a result of this promotion.</summary>
    public long ToGeneration { get; set; }

    /// <summary>Whether this was a forward <c>promote</c> or a <c>rollback</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Authenticated admin identity that performed the promotion.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Optional operator-supplied reason.</summary>
    public string? Reason { get; set; }

    /// <summary>Integrity-evidence digest of the generation that became active.</summary>
    public string? EvidenceDigest { get; set; }

    /// <summary>When this promotion committed.</summary>
    public DateTimeOffset PromotedAt { get; set; }
}
