// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching;

public enum MetadataCacheStatus
{
    Hit,

    Miss,

    Stale,

    Refreshed,

    Bypass,

    Failed
}

public sealed record MetadataCacheValidators
{
    public static readonly MetadataCacheValidators None = new();

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public bool HasValidators => !string.IsNullOrWhiteSpace(ETag) || LastModified.HasValue;
}

public sealed record MetadataCacheState
{
    public required MetadataCacheStatus Status { get; init; }

    public required string KeyFingerprint { get; init; }

    public TimeSpan? Age { get; init; }

    public TimeSpan? Ttl { get; init; }

    public TimeSpan? StaleIfError { get; init; }

    public MetadataCacheValidators Validators { get; init; } = MetadataCacheValidators.None;

    public DateTimeOffset? RevalidateAfter { get; init; }

    public string? InvalidationReason { get; init; }

    public string? RefreshErrorId { get; init; }

    public bool HasUsableEntry => Status is MetadataCacheStatus.Hit or MetadataCacheStatus.Stale or MetadataCacheStatus.Refreshed;
}
