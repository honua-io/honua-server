// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

public sealed record ShareItemRef
{
    public string? ResourceId { get; init; }

    public required string ServiceName { get; init; }

    public required int LayerId { get; init; }
}

public sealed record ShareTrafficCounts
{
    public long Public { get; init; }

    public long PublicLink { get; init; }

    public long Embed { get; init; }

    public long OpenData { get; init; }

    public long Dcat { get; init; }

    public long Stac { get; init; }

    public long Export { get; init; }
}

public sealed record ShareTrafficSummary
{
    public ShareItemRef? ItemRef { get; init; }

    public required DateTimeOffset PeriodStart { get; init; }

    public required DateTimeOffset PeriodEnd { get; init; }

    public required ShareTrafficCounts ByInteractionType { get; init; }

    public long TotalRequests { get; init; }
}

public sealed record ShareTrafficBucket
{
    public required DateTimeOffset BucketStart { get; init; }

    public required ShareTrafficCounts ByInteractionType { get; init; }

    public long Total { get; init; }
}

public sealed record ShareTrafficSeries
{
    public ShareItemRef? ItemRef { get; init; }

    public required DateTimeOffset PeriodStart { get; init; }

    public required DateTimeOffset PeriodEnd { get; init; }

    public required TimeSpan BucketDuration { get; init; }

    public required IReadOnlyList<ShareTrafficBucket> Buckets { get; init; }
}

public sealed record ShareTrafficQuery
{
    public ShareItemRef? ItemRef { get; init; }

    public required DateTimeOffset PeriodStart { get; init; }

    public required DateTimeOffset PeriodEnd { get; init; }

    public TimeSpan BucketDuration { get; init; } = TimeSpan.FromHours(1);
}
