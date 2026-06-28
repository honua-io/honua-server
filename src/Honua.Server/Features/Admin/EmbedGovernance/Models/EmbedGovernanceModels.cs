// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.EmbedGovernance.Models;

/// <summary>
/// Scope payload describing what an embed key may do.
/// </summary>
public sealed class EmbedKeyScopeDto
{
    /// <summary>Browser origins permitted to load the embed.</summary>
    public IReadOnlyList<string> AllowedEmbedOrigins { get; init; } = [];

    /// <summary>Service identifiers/origins the embed may call.</summary>
    public IReadOnlyList<string> AllowedServiceOrigins { get; init; } = [];

    /// <summary>Content/item identifiers the embed may render.</summary>
    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    /// <summary>Tenant the key is bound to.</summary>
    public string? TenantId { get; init; }

    /// <summary>Integration the key represents.</summary>
    public string? IntegrationId { get; init; }

    /// <summary>Edition entitlement the key is issued under.</summary>
    public string? Edition { get; init; }

    /// <summary>Maximum embed requests allowed per window; zero disables limiting.</summary>
    public int RateLimitRequestsPerWindow { get; init; }

    /// <summary>Length of the rate window in seconds; defaults to 60.</summary>
    public int RateLimitWindowSeconds { get; init; } = 60;
}

/// <summary>
/// Request body for creating an embed key.
/// </summary>
public sealed class CreateEmbedKeyRequest
{
    /// <summary>Human-readable key name used in operator audit and list views.</summary>
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>Authoritative scope for the key.</summary>
    [Required]
    public required EmbedKeyScopeDto Scope { get; init; }

    /// <summary>Optional UTC expiration time for the key.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Metadata for an embed key without plaintext key material.
/// </summary>
public sealed class EmbedKeyResponse
{
    /// <summary>Stable key identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Human-readable key name.</summary>
    public required string Name { get; init; }

    /// <summary>Non-secret key prefix used for operator recognition.</summary>
    public required string KeyPrefix { get; init; }

    /// <summary>Current key lifecycle status.</summary>
    public required string Status { get; init; }

    /// <summary>Authoritative key scope.</summary>
    public required EmbedKeyScopeDto Scope { get; init; }

    /// <summary>When the key was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the key metadata was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional key expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Most recent successful authentication time.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Most recent key rotation time.</summary>
    public DateTimeOffset? RotatedAt { get; init; }

    /// <summary>Revocation time when revoked.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Authenticated principal that created the key when available.</summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Create or rotate response that includes one-time plaintext key material.
/// </summary>
public sealed class EmbedKeySecretResponse
{
    /// <summary>Key metadata without secret material.</summary>
    public required EmbedKeyResponse EmbedKey { get; init; }

    /// <summary>Plaintext key material. Returned only at create and rotate time.</summary>
    public required string Key { get; init; }
}

/// <summary>
/// Advertised rate budget for embed traffic.
/// </summary>
public sealed class EmbedRateLimitResponse
{
    /// <summary>Maximum requests allowed per window; zero means unlimited.</summary>
    public int RequestsPerWindow { get; init; }

    /// <summary>Length of the window in seconds.</summary>
    public int WindowSeconds { get; init; }
}

/// <summary>
/// Policy payload consumed by the <c>@honua-io/embed</c> governance adapter.
/// </summary>
public sealed class EmbedPolicyResponse
{
    /// <summary>Integration the key represents, when scoped.</summary>
    public string? IntegrationId { get; init; }

    /// <summary>Tenant the key is bound to, when scoped.</summary>
    public string? TenantId { get; init; }

    /// <summary>Edition entitlement the key is issued under, when set.</summary>
    public string? Edition { get; init; }

    /// <summary>Browser origins the embed may load under.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>Service identifiers/origins the embed may call.</summary>
    public IReadOnlyList<string> AllowedServices { get; init; } = [];

    /// <summary>Content identifiers the embed may render.</summary>
    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    /// <summary>Capabilities the embed is permitted to invoke.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Advertised rate budget for embed traffic.</summary>
    public required EmbedRateLimitResponse RateLimit { get; init; }
}

/// <summary>
/// A single redacted analytics event posted by an embed client.
/// </summary>
public sealed class EmbedAnalyticsEventDto
{
    /// <summary>Event category: view, configChange, search, identify, policyDenial.</summary>
    [Required]
    public required string EventType { get; init; }

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

    /// <summary>Deny reason when the event is a policy denial.</summary>
    public string? DenyReason { get; init; }

    /// <summary>When the event occurred (UTC). Defaults to ingest time when omitted.</summary>
    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>
/// Batch of redacted analytics events. Raw browser key material must never be
/// present in any field.
/// </summary>
public sealed class IngestEmbedAnalyticsRequest
{
    /// <summary>The events to ingest.</summary>
    [Required]
    public required IReadOnlyList<EmbedAnalyticsEventDto> Events { get; init; }
}

/// <summary>
/// Result of an analytics ingestion request.
/// </summary>
public sealed class EmbedAnalyticsIngestResponse
{
    /// <summary>Number of events accepted.</summary>
    public int Accepted { get; init; }
}

/// <summary>
/// A single grouped usage count.
/// </summary>
public sealed class EmbedUsageAggregateDto
{
    /// <summary>The grouping key value.</summary>
    public required string Key { get; init; }

    /// <summary>Number of events in the group.</summary>
    public long Count { get; init; }
}

/// <summary>
/// Usage aggregation report for operator/Console reporting.
/// </summary>
public sealed class EmbedUsageResponse
{
    /// <summary>The dimension counts were grouped by.</summary>
    public required string GroupBy { get; init; }

    /// <summary>Total matching events across all groups.</summary>
    public long Total { get; init; }

    /// <summary>Per-group counts, descending by count.</summary>
    public IReadOnlyList<EmbedUsageAggregateDto> Aggregates { get; init; } = [];
}
