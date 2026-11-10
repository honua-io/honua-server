// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

public enum EmbedAnalyticsEventType
{
    View = 0,

    ConfigChange = 1,

    Search = 2,

    Identify = 3,

    PolicyDenial = 4,
}

// The event carries the issued key id, never the raw browser credential.
public sealed record EmbedAnalyticsEvent
{
    public required EmbedAnalyticsEventType EventType { get; init; }

    public Guid? KeyId { get; init; }

    public string? IntegrationId { get; init; }

    public string? TenantId { get; init; }

    public string? Origin { get; init; }

    public string? ServiceId { get; init; }

    public string? LayerId { get; init; }

    public EmbedPolicyDenyReason? DenyReason { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

public enum EmbedUsageDimension
{
    Integration = 0,

    Tenant = 1,

    Origin = 2,

    Service = 3,

    Layer = 4,

    EventType = 5,
}

public sealed record EmbedUsageQuery
{
    public EmbedUsageDimension GroupBy { get; init; } = EmbedUsageDimension.EventType;

    public string? IntegrationId { get; init; }

    public string? TenantId { get; init; }

    public string? Origin { get; init; }

    public string? ServiceId { get; init; }

    public string? LayerId { get; init; }

    public EmbedAnalyticsEventType? EventType { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }
}

public sealed record EmbedUsageAggregate
{
    public required string Key { get; init; }

    public required long Count { get; init; }
}

public sealed record EmbedUsageReport
{
    public required EmbedUsageDimension GroupBy { get; init; }

    public required long Total { get; init; }

    public IReadOnlyList<EmbedUsageAggregate> Aggregates { get; init; } = [];
}
