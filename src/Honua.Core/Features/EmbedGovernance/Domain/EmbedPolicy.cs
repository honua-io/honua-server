// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Rate budget advertised to an embed client. Client-side enforcement is a hint
/// only; the authoritative limit is enforced server-side.
/// </summary>
public sealed record EmbedRateLimitPolicy
{
    /// <summary>Maximum requests allowed per window. Zero means unlimited.</summary>
    public required int RequestsPerWindow { get; init; }

    /// <summary>Length of the window in seconds.</summary>
    public required int WindowSeconds { get; init; }
}

/// <summary>
/// Policy payload returned to the <c>@honua-io/embed</c> remote governance
/// adapter (consumed by <c>fetchHonuaMapEmbedPolicy</c>). It mirrors the
/// authoritative scope of the embed key so the client can render only what the
/// server will allow, while the server remains the security boundary.
/// </summary>
public sealed record EmbedPolicy
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
    public required EmbedRateLimitPolicy RateLimit { get; init; }
}
