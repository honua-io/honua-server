// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

public sealed record FeatureChangeOutboxEntry
{
    public required Guid OutboxId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required long ObjectId { get; init; }

    public required string Operation { get; init; }

    public required string Protocol { get; init; }

    public string? SourceId { get; init; }

    public required string RequestId { get; init; }

    // The same event id is reused on retry so consumers can deduplicate.
    public required string EventId { get; init; }

    public string? OperationInstanceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public required string EventPayload { get; init; }

    public required string Status { get; init; }

    public required int RetryCount { get; init; }

    public string? LastError { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    public string? ClaimNodeId { get; init; }

    public DateTimeOffset? ClaimExpiresAt { get; init; }

    public DateTimeOffset? DispatchedAt { get; init; }
}
