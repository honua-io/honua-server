// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    /// Optional permission labels assigned to the key.
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    /// Optional UTC expiration time for the key.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
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
