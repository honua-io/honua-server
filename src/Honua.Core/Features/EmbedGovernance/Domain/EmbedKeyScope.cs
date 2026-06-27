// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Authoritative scope attached to an embed API key. Defines the origins,
/// services, content, tenant, and rate budget an embedded map surface may use.
/// </summary>
/// <remarks>
/// Allowed-origin matching is security critical: an empty
/// <see cref="AllowedEmbedOrigins"/> list denies every browser origin. Service
/// and content lists narrow access when populated but allow everything when
/// empty, so an integration can be scoped to specific origins without having to
/// enumerate every service or content id up front.
/// </remarks>
public sealed record EmbedKeyScope
{
    /// <summary>
    /// Browser origins (scheme + host[:port]) permitted to load the embed.
    /// Supports an exact origin, a <c>*.example.com</c> subdomain wildcard, or
    /// <c>*</c> for any origin. An empty list denies all origins.
    /// </summary>
    public IReadOnlyList<string> AllowedEmbedOrigins { get; init; } = [];

    /// <summary>
    /// Service identifiers (or service origins) the embed may call. An empty
    /// list permits any service; <c>*</c> also permits any service.
    /// </summary>
    public IReadOnlyList<string> AllowedServiceOrigins { get; init; } = [];

    /// <summary>
    /// Content/item identifiers (maps, layers, scenes) the embed may render. An
    /// empty list permits any content id; <c>*</c> also permits any content.
    /// </summary>
    public IReadOnlyList<string> AllowedContentIds { get; init; } = [];

    /// <summary>
    /// Tenant the key is bound to. When set, requests asserting a different
    /// tenant are denied. <c>null</c> leaves the key tenant-agnostic.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Integration the key represents (e.g. a customer site or app). Surfaced in
    /// analytics and policy payloads.
    /// </summary>
    public string? IntegrationId { get; init; }

    /// <summary>
    /// Edition entitlement the key is issued under (e.g. <c>pro</c>,
    /// <c>enterprise</c>). Informational; surfaced in the policy payload.
    /// </summary>
    public string? Edition { get; init; }

    /// <summary>
    /// Maximum embed requests allowed per <see cref="RateLimitWindow"/>. A value
    /// of zero (the default) disables rate limiting for the key.
    /// </summary>
    public int RateLimitRequestsPerWindow { get; init; }

    /// <summary>
    /// Fixed window over which <see cref="RateLimitRequestsPerWindow"/> applies.
    /// Defaults to one minute.
    /// </summary>
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);
}
