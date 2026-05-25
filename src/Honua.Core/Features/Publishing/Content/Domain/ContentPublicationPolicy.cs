// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Publishing.Content.Domain;

/// <summary>
/// Server-owned access/share/embed/public-link policy for a published route. This
/// is the authoritative source of truth; Console must not treat publish/share/embed
/// state as local UI state. A snapshot of this policy is also captured on each
/// immutable version at publish time.
/// </summary>
public sealed record ContentPublicationPolicy
{
    /// <summary>Visibility scope of the route. Defaults to <see cref="ContentPublicationVisibility.Private"/>.</summary>
    [JsonPropertyName("visibility")]
    public ContentPublicationVisibility Visibility { get; init; } = ContentPublicationVisibility.Private;

    /// <summary>
    /// Role/anonymous access policy enforced by backend reads via the shared
    /// <c>IAccessPolicyEvaluator</c>. When null, visibility alone governs access.
    /// </summary>
    [JsonPropertyName("access")]
    public AccessPolicy? Access { get; init; }

    /// <summary>Sharing policy.</summary>
    [JsonPropertyName("share")]
    public ContentSharePolicy Share { get; init; } = new();

    /// <summary>Embedding policy.</summary>
    [JsonPropertyName("embed")]
    public ContentEmbedPolicy Embed { get; init; } = new();

    /// <summary>Backing-service policy.</summary>
    [JsonPropertyName("service")]
    public ContentServicePolicy Service { get; init; } = new();

    /// <summary>Public-link policy. Disabled by default; tokens are stored as hashes only.</summary>
    [JsonPropertyName("publicLink")]
    public ContentPublicLinkPolicy PublicLink { get; init; } = new();
}

/// <summary>Sharing policy for a published route.</summary>
public sealed record ContentSharePolicy
{
    /// <summary>When true, the artifact may be shared with other principals.</summary>
    [JsonPropertyName("allowSharing")]
    public bool AllowSharing { get; init; }

    /// <summary>When true, anonymous (unauthenticated) reads are permitted via the route.</summary>
    [JsonPropertyName("allowAnonymous")]
    public bool AllowAnonymous { get; init; }

    /// <summary>Optional explicit scopes the artifact may be shared with (org/team ids).</summary>
    [JsonPropertyName("allowedScopes")]
    public IReadOnlyList<string>? AllowedScopes { get; init; }
}

/// <summary>Embedding policy. Default denies embedding.</summary>
public sealed record ContentEmbedPolicy
{
    /// <summary>Allow/deny flag for embedding the artifact in an iframe.</summary>
    [JsonPropertyName("allowEmbedding")]
    public bool AllowEmbedding { get; init; }

    /// <summary>Allowed embedding origins (used to gate embed reads and emit CSP/CORS hints).</summary>
    [JsonPropertyName("allowedOrigins")]
    public IReadOnlyList<string>? AllowedOrigins { get; init; }

    /// <summary>Allowed frame ancestors for the embedded artifact.</summary>
    [JsonPropertyName("frameAncestors")]
    public IReadOnlyList<string>? FrameAncestors { get; init; }
}

/// <summary>Policy describing how the artifact's backing services are exposed.</summary>
public sealed record ContentServicePolicy
{
    /// <summary>When true, backing service reads still require authentication even for a public route.</summary>
    [JsonPropertyName("requireAuthenticatedServices")]
    public bool RequireAuthenticatedServices { get; init; }

    /// <summary>Optional explicit list of backing service ids the artifact exposes.</summary>
    [JsonPropertyName("allowedServiceIds")]
    public IReadOnlyList<string>? AllowedServiceIds { get; init; }
}

/// <summary>
/// Public-link policy. Public-link support is security sensitive: it is disabled by
/// default, and link records store only a stable unguessable id plus an optional
/// hash of the link token. The raw token is never persisted.
/// </summary>
public sealed record ContentPublicLinkPolicy
{
    /// <summary>Master enable flag. When false, all public-link reads are denied.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Issued public links (ids and hashes only).</summary>
    [JsonPropertyName("links")]
    public IReadOnlyList<ContentPublicLink> Links { get; init; } = [];
}

/// <summary>
/// A single public link. Stores a stable id and an optional token hash only;
/// the raw token is returned once at creation time and never stored or echoed.
/// </summary>
public sealed record ContentPublicLink
{
    /// <summary>Stable, unguessable link id used in the read request.</summary>
    [JsonPropertyName("linkId")]
    public required string LinkId { get; init; }

    /// <summary>Base64 hash of the optional link token. Never the raw token.</summary>
    [JsonPropertyName("tokenHash")]
    public string? TokenHash { get; init; }

    /// <summary>Hash algorithm used for <see cref="TokenHash"/> (e.g. <c>SHA-256</c>).</summary>
    [JsonPropertyName("tokenHashAlgorithm")]
    public string? TokenHashAlgorithm { get; init; }

    /// <summary>Optional human-readable label.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Actor that created the link.</summary>
    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    /// <summary>Creation time.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optional expiry. Reads after this instant are denied.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When true, the link is revoked and reads are denied.</summary>
    [JsonPropertyName("revoked")]
    public bool Revoked { get; init; }

    /// <summary>Revocation time, when revoked.</summary>
    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; init; }
}
