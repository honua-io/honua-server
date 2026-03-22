// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Licensing.Domain;

/// <summary>
/// Represents the current license state of the server instance.
/// </summary>
public sealed class LicenseInfo
{
    /// <summary>
    /// The server edition (e.g., Community, Professional, Enterprise).
    /// </summary>
    public required string Edition { get; init; }

    /// <summary>
    /// When the license expires, or null for perpetual/community licenses.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Whether the license has been validated successfully.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Human-readable validation state (e.g., "Valid", "Expired", "Invalid Signature").
    /// </summary>
    public required string ValidationState { get; init; }

    /// <summary>
    /// The licensee organization name.
    /// </summary>
    public string? LicensedTo { get; init; }

    /// <summary>
    /// When the license was issued.
    /// </summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>
    /// Entitlements granted by this license.
    /// </summary>
    public IReadOnlyList<Entitlement> Entitlements { get; init; } = [];
}

/// <summary>
/// A single feature entitlement granted by a license.
/// </summary>
public sealed class Entitlement
{
    /// <summary>
    /// Unique key for this entitlement (e.g., "oidc", "rbac", "rate-limiting").
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable name for this entitlement.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether this entitlement is currently active.
    /// </summary>
    public bool IsActive { get; init; }
}
