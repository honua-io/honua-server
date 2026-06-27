// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.EmbedGovernance.Domain;

/// <summary>
/// Lifecycle status of an embed API key.
/// </summary>
public enum EmbedKeyStatus
{
    /// <summary>The key can authenticate and issue policy.</summary>
    Active = 0,

    /// <summary>The key has passed its expiration time.</summary>
    Expired = 1,

    /// <summary>The key was explicitly revoked by an operator.</summary>
    Revoked = 2,
}

/// <summary>
/// Persisted embed API key without plaintext key material. The raw key is only
/// ever returned once, at create and rotate time.
/// </summary>
public sealed record EmbedKeyRecord
{
    /// <summary>Stable key identifier.</summary>
    public required Guid Id { get; init; }

    /// <summary>Human-readable name used in operator list/audit views.</summary>
    public required string Name { get; init; }

    /// <summary>Non-secret key prefix used for operator recognition.</summary>
    public required string KeyPrefix { get; init; }

    /// <summary>SHA-256 hash of the plaintext key material.</summary>
    public required byte[] KeyHash { get; init; }

    /// <summary>Authoritative scope governing what the key may do.</summary>
    public required EmbedKeyScope Scope { get; init; }

    /// <summary>When the key was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the key metadata was last updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional UTC expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Most recent successful policy/authentication time.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Most recent rotation time.</summary>
    public DateTimeOffset? RotatedAt { get; init; }

    /// <summary>Revocation time when revoked.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Authenticated principal that created the key when available.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Resolves the lifecycle status of the key at the supplied instant.
    /// </summary>
    /// <param name="now">The reference instant.</param>
    /// <returns>The effective <see cref="EmbedKeyStatus"/>.</returns>
    public EmbedKeyStatus GetStatus(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return EmbedKeyStatus.Revoked;
        }

        if (ExpiresAt.HasValue && ExpiresAt.Value <= now)
        {
            return EmbedKeyStatus.Expired;
        }

        return EmbedKeyStatus.Active;
    }
}
