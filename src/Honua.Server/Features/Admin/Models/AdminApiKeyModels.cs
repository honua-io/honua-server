// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request body for creating an admin API key.
/// </summary>
public sealed class CreateAdminApiKeyRequest
{
    /// <summary>
    /// Human-readable key name used in operator audit and list views.
    /// </summary>
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Optional permission labels assigned to the key. A key whose grants are all
    /// layer-scoped write grants (<c>write:{service}</c> or
    /// <c>write:{service}/{layer}</c>) and that carries no full-admin grant
    /// (<c>admin</c> or <c>*</c>) is minted as a low-blast-radius, write-only,
    /// layer-scoped credential (#1637): it authenticates as a non-admin principal
    /// and is authorized only for writes to the named service(s)/layer(s). Data read
    /// grants use <c>read:{service}</c>, <c>read:{service}/{layer}</c>,
    /// <c>read:{service}/*</c> or <c>read:*</c>; wildcards are rejected on
    /// <c>write:</c> grants.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    /// Optional UTC expiration time for the key.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Optional origin allow-list. Each entry is <c>scheme://host[:port]</c>, optionally with a
    /// leading <c>*.</c> label that matches any subdomain (not the bare domain). When set, a
    /// request is accepted only if its <c>Origin</c> header (or, failing that, its
    /// <c>Referer</c> reduced to an origin) matches an entry; a request with neither header is
    /// refused. Recommended for keys used from browsers or in the <c>token</c> query parameter.
    /// </summary>
    public IReadOnlyList<string>? AllowedReferrers { get; init; }

    /// <summary>
    /// Optional client IP allow-list in CIDR notation (<c>203.0.113.0/24</c>,
    /// <c>2001:db8::/32</c>) or single addresses. When set, a request is accepted only from an
    /// address in one of the ranges, as resolved behind the configured trusted proxies.
    /// </summary>
    public IReadOnlyList<string>? AllowedIpRanges { get; init; }
}

/// <summary>
/// Replacement referrer and IP restrictions for an existing admin API key.
/// </summary>
/// <remarks>
/// Each list replaces that restriction entirely. Null or empty removes it. The key secret
/// is not part of this request and is not returned.
/// </remarks>
public sealed class UpdateAdminApiKeyRestrictionsRequest
{
    /// <summary>
    /// Origin allow-list that replaces the key's current referrer restriction.
    /// </summary>
    public IReadOnlyList<string>? AllowedReferrers { get; init; }

    /// <summary>
    /// CIDR allow-list that replaces the key's current IP restriction.
    /// </summary>
    public IReadOnlyList<string>? AllowedIpRanges { get; init; }
}

/// <summary>
/// Metadata for an admin API key without plaintext key material.
/// </summary>
public sealed class AdminApiKeyResponse
{
    /// <summary>
    /// Stable key identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Human-readable key name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Non-secret key prefix used for operator recognition.
    /// </summary>
    public required string KeyPrefix { get; init; }

    /// <summary>
    /// Assigned permission labels.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    /// Current key lifecycle status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// When the key was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the key metadata was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Optional key expiration time.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Most recent successful authentication time.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>
    /// Most recent key rotation time.
    /// </summary>
    public DateTimeOffset? RotatedAt { get; init; }

    /// <summary>
    /// Revocation time when revoked.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>
    /// Authenticated principal that created the key when available.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Origin patterns the request must match; absent when the key has no referrer restriction.
    /// </summary>
    public IReadOnlyList<string>? AllowedReferrers { get; init; }

    /// <summary>
    /// CIDR ranges the client address must fall in; absent when the key has no IP restriction.
    /// </summary>
    public IReadOnlyList<string>? AllowedIpRanges { get; init; }
}

/// <summary>
/// Create or rotate response that includes one-time plaintext key material.
/// </summary>
public sealed class AdminApiKeySecretResponse
{
    /// <summary>
    /// Key metadata without secret material.
    /// </summary>
    public required AdminApiKeyResponse ApiKey { get; init; }

    /// <summary>
    /// Plaintext key material. Returned only at create and rotate time.
    /// </summary>
    public required string Key { get; init; }
}

/// <summary>
/// Effective permissions response for an admin API key.
/// </summary>
public sealed class AdminApiKeyEffectivePermissionsResponse
{
    /// <summary>
    /// Key identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Key name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Current key lifecycle status.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Permissions currently assigned to the key.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    /// Whether the key can currently authenticate.
    /// </summary>
    public bool CanAuthenticate { get; init; }
}
