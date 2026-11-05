// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

// An empty origin list denies every browser origin, while an empty service list does not.
public sealed record EmbedKeyScope
{
    public IReadOnlyList<string> AllowedEmbedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedServiceOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    public string? TenantId { get; init; }

    public string? IntegrationId { get; init; }

    public string? Edition { get; init; }

    public int RateLimitRequestsPerWindow { get; init; }

    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);
}
