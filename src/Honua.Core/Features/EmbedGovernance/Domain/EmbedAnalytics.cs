// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Category of redacted analytics event emitted by an embedded map surface.
/// </summary>
public enum EmbedAnalyticsEventType
{
    /// <summary>The embed was loaded/viewed.</summary>
    View = 0,

    /// <summary>The embed configuration changed (layers, extent, style).</summary>
    ConfigChange = 1,

    /// <summary>A search/geocode was performed in the embed.</summary>
    Search = 2,

    /// <summary>An identify/feature-info action was performed.</summary>
    Identify = 3,

    /// <summary>A policy denial was recorded for the embed.</summary>
    PolicyDenial = 4,
}

/// <summary>
/// A single redacted embed analytics event. By contract these events never carry
/// raw browser API-key material; the key is identified server-side from the
/// transport credential and surfaced here only as the issued key id.
/// </summary>
public sealed record EmbedAnalyticsEvent
{
    /// <summary>The category of event.</summary>
    public required EmbedAnalyticsEventType EventType { get; init; }

    /// <summary>Issued embed key id the event is attributed to (never the raw key).</summary>
    public Guid? KeyId { get; init; }

    /// <summary>Integration the event belongs to.</summary>
    public string? IntegrationId { get; init; }

    /// <summary>Tenant the event belongs to.</summary>
    public string? TenantId { get; init; }

    /// <summary>Browser origin the event originated from.</summary>
    public string? Origin { get; init; }

    /// <summary>Service the event targeted.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Layer/content the event targeted.</summary>
    public string? LayerId { get; init; }

    /// <summary>Deny reason when <see cref="EventType"/> is a policy denial.</summary>
    public EmbedPolicyDenyReason? DenyReason { get; init; }

    /// <summary>When the event occurred (UTC).</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// Dimension to group embed usage by when querying aggregates.
/// </summary>
public enum EmbedUsageDimension
{
    /// <summary>Group by integration.</summary>
    Integration = 0,

    /// <summary>Group by tenant.</summary>
    Tenant = 1,

    /// <summary>Group by browser origin.</summary>
    Origin = 2,

    /// <summary>Group by service.</summary>
    Service = 3,

    /// <summary>Group by layer/content.</summary>
    Layer = 4,

    /// <summary>Group by event type.</summary>
    EventType = 5,
}

/// <summary>
/// Filter and grouping inputs for a usage aggregation query.
/// </summary>
public sealed record EmbedUsageQuery
{
    /// <summary>Dimension to group counts by.</summary>
    public EmbedUsageDimension GroupBy { get; init; } = EmbedUsageDimension.EventType;

    /// <summary>Optional integration filter.</summary>
    public string? IntegrationId { get; init; }

    /// <summary>Optional tenant filter.</summary>
    public string? TenantId { get; init; }

    /// <summary>Optional origin filter.</summary>
    public string? Origin { get; init; }

    /// <summary>Optional service filter.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Optional layer filter.</summary>
    public string? LayerId { get; init; }

    /// <summary>Optional event-type filter.</summary>
    public EmbedAnalyticsEventType? EventType { get; init; }

    /// <summary>Optional inclusive lower time bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Optional exclusive upper time bound.</summary>
    public DateTimeOffset? To { get; init; }
}

/// <summary>
/// A single grouped usage count.
/// </summary>
public sealed record EmbedUsageAggregate
{
    /// <summary>The grouping key value (e.g. integration id, origin, event type).</summary>
    public required string Key { get; init; }

    /// <summary>Number of events in the group.</summary>
    public required long Count { get; init; }
}

/// <summary>
/// Result of a usage aggregation query.
/// </summary>
public sealed record EmbedUsageReport
{
    /// <summary>The dimension counts were grouped by.</summary>
    public required EmbedUsageDimension GroupBy { get; init; }

    /// <summary>Total matching events across all groups.</summary>
    public required long Total { get; init; }

    /// <summary>Per-group counts, descending by count.</summary>
    public IReadOnlyList<EmbedUsageAggregate> Aggregates { get; init; } = [];
}
