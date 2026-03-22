// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Identity.Domain;

/// <summary>
/// Represents a user provisioned via OIDC or SCIM.
/// </summary>
public sealed class ManagedUser
{
    /// <summary>
    /// Unique identifier for this user.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Email address.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// How this user was provisioned (e.g., "oidc", "scim", "manual").
    /// </summary>
    public required string ProvisioningSource { get; init; }

    /// <summary>
    /// The OIDC provider ID that provisioned this user, if applicable.
    /// </summary>
    public Guid? ProviderId { get; init; }

    /// <summary>
    /// Whether the user account is active.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Role names assigned to this user.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// When the user was first provisioned.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the user record was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
