// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

/// <summary>
/// Reference to the Share item a traffic projection is scoped to. A null reference
/// denotes the aggregate (whole-tenant) projection.
/// </summary>
public sealed record ShareItemRef
{
    /// <summary>Optional stable Console content/resource identifier for the item.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Service name of the item.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Layer identifier of the item.</summary>
    public required int LayerId { get; init; }
}

/// <summary>
/// Per-interaction-type request counts. The named fields are a stable, source-generated
/// JSON shape (no dictionary keyed by enum) so SDK projection stays trim/AOT safe.
/// </summary>
public sealed record ShareTrafficCounts
{
    /// <summary>Direct public access requests.</summary>
    public long Public { get; init; }

    /// <summary>Public-link access requests.</summary>
    public long PublicLink { get; init; }

    /// <summary>Embed access requests.</summary>
    public long Embed { get; init; }

    /// <summary>Open-data portal access requests.</summary>
    public long OpenData { get; init; }

    /// <summary>DCAT catalog access requests.</summary>
    public long Dcat { get; init; }

    /// <summary>STAC catalog access requests.</summary>
    public long Stac { get; init; }

    /// <summary>Export interaction requests.</summary>
    public long Export { get; init; }
}

/// <summary>
/// Aggregate or per-item Share traffic summary over a closed period.
/// </summary>
public sealed record ShareTrafficSummary
{
    /// <summary>Item the summary is scoped to, or null for the aggregate projection.</summary>
    public ShareItemRef? ItemRef { get; init; }

    /// <summary>Inclusive start of the reporting period (UTC).</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>Exclusive end of the reporting period (UTC).</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Request counts broken down by interaction type.</summary>
    public required ShareTrafficCounts ByInteractionType { get; init; }

    /// <summary>Total requests across all interaction types.</summary>
    public long TotalRequests { get; init; }
}

/// <summary>
/// A single time bucket within a Share traffic time series.
/// </summary>
public sealed record ShareTrafficBucket
{
    /// <summary>Inclusive start of the bucket (UTC).</summary>
    public required DateTimeOffset BucketStart { get; init; }

    /// <summary>Request counts broken down by interaction type within the bucket.</summary>
    public required ShareTrafficCounts ByInteractionType { get; init; }

    /// <summary>Total requests within the bucket.</summary>
    public long Total { get; init; }
}

/// <summary>
/// Aggregate or per-item Share traffic time series.
/// </summary>
public sealed record ShareTrafficSeries
{
    /// <summary>Item the series is scoped to, or null for the aggregate projection.</summary>
    public ShareItemRef? ItemRef { get; init; }

    /// <summary>Inclusive start of the reporting period (UTC).</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>Exclusive end of the reporting period (UTC).</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Width of each time bucket.</summary>
    public required TimeSpan BucketDuration { get; init; }

    /// <summary>Ordered, contiguous time buckets covering the period.</summary>
    public required IReadOnlyList<ShareTrafficBucket> Buckets { get; init; }
}

/// <summary>
/// Read criteria for Share traffic summary and series projections.
/// </summary>
public sealed record ShareTrafficQuery
{
    /// <summary>Item to scope to, or null for the aggregate projection.</summary>
    public ShareItemRef? ItemRef { get; init; }

    /// <summary>Inclusive start of the reporting period (UTC).</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>Exclusive end of the reporting period (UTC).</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>
    /// Width of each time bucket for series reads. Ignored by summary reads.
    /// </summary>
    public TimeSpan BucketDuration { get; init; } = TimeSpan.FromHours(1);
}
