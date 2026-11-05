// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Routing;

public sealed class NetworkTopologyRebuildSubmissionDto
{
    public string OperationId { get; set; } = string.Empty;

    public long Generation { get; set; }

    public long Attempt { get; set; }
}

public sealed class NetworkTopologyRebuildCheckpointDto
{
    public string Stage { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    // Stage detail never includes raw geometry or SQL.
    public string? Detail { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NetworkTopologyRebuildAttemptDto
{
    public string DatasetId { get; set; } = string.Empty;

    public long Generation { get; set; }

    public long Attempt { get; set; }

    public string State { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string? FailureCode { get; set; }

    public string? EvidenceDigest { get; set; }

    public string? OwnerId { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public NetworkTopologyRebuildCheckpointDto[] Checkpoints { get; set; } = [];
}

public sealed class NetworkTopologyPromotionRequest
{
    // Promotion requires a ready generation and rollback requires a retired generation.
    public long? TargetGeneration { get; set; }

    public long? ExpectedActiveGeneration { get; set; }

    public long? ExpectedActiveRowVersion { get; set; }

    public string? Reason { get; set; }
}

public sealed class NetworkTopologyPromotionDto
{
    public string PromotionId { get; set; } = string.Empty;

    public string DatasetId { get; set; } = string.Empty;

    public long? FromGeneration { get; set; }

    public long ToGeneration { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? EvidenceDigest { get; set; }

    public DateTimeOffset PromotedAt { get; set; }
}
