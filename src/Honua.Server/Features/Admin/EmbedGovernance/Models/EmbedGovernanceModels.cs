// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.EmbedGovernance.Models;

public sealed class EmbedKeyScopeDto
{
    public IReadOnlyList<string> AllowedEmbedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedServiceOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    public string? TenantId { get; init; }

    public string? IntegrationId { get; init; }

    public string? Edition { get; init; }

    public int RateLimitRequestsPerWindow { get; init; }

    public int RateLimitWindowSeconds { get; init; } = 60;
}

public sealed class CreateEmbedKeyRequest
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required]
    public required EmbedKeyScopeDto Scope { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class EmbedKeyResponse
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public required string KeyPrefix { get; init; }

    public required string Status { get; init; }

    public required EmbedKeyScopeDto Scope { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public DateTimeOffset? RotatedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed class EmbedKeySecretResponse
{
    public required EmbedKeyResponse EmbedKey { get; init; }

    public required string Key { get; init; }
}

public sealed class EmbedRateLimitResponse
{
    public int RequestsPerWindow { get; init; }

    public int WindowSeconds { get; init; }
}

public sealed class EmbedPolicyResponse
{
    public string? IntegrationId { get; init; }

    public string? TenantId { get; init; }

    public string? Edition { get; init; }

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> AllowedServices { get; init; } = [];

    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public required EmbedRateLimitResponse RateLimit { get; init; }
}

public sealed class EmbedAnalyticsEventDto
{
    [Required]
    public required string EventType { get; init; }

    public string? IntegrationId { get; init; }

    public string? TenantId { get; init; }

    public string? Origin { get; init; }

    public string? ServiceId { get; init; }

    public string? LayerId { get; init; }

    public string? DenyReason { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }
}

// A redacted analytics batch must never carry raw browser key material.
public sealed class IngestEmbedAnalyticsRequest
{
    [Required]
    public required IReadOnlyList<EmbedAnalyticsEventDto> Events { get; init; }
}

public sealed class EmbedAnalyticsIngestResponse
{
    public int Accepted { get; init; }
}

public sealed class EmbedUsageAggregateDto
{
    public required string Key { get; init; }

    public long Count { get; init; }
}

public sealed class EmbedUsageResponse
{
    public required string GroupBy { get; init; }

    public long Total { get; init; }

    public IReadOnlyList<EmbedUsageAggregateDto> Aggregates { get; init; } = [];
}
