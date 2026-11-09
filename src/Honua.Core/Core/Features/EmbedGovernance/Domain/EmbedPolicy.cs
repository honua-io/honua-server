// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

// The client budget is a hint; the server enforces the limit.
public sealed record EmbedRateLimitPolicy
{
    public required int RequestsPerWindow { get; init; }

    public required int WindowSeconds { get; init; }
}

public sealed record EmbedPolicy
{
    public string? IntegrationId { get; init; }

    public string? TenantId { get; init; }

    public string? Edition { get; init; }

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedServices { get; init; } = [];

    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public required EmbedRateLimitPolicy RateLimit { get; init; }
}
