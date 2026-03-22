// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for license status.
/// </summary>
public sealed class LicenseStatusResponse
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
    /// Whether the license is currently valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Validation state description.
    /// </summary>
    public required string ValidationState { get; init; }

    /// <summary>
    /// Organization the license is issued to.
    /// </summary>
    public string? LicensedTo { get; init; }

    /// <summary>
    /// When the license was issued.
    /// </summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>
    /// Entitlements granted by this license.
    /// </summary>
    public IReadOnlyList<EntitlementResponse> Entitlements { get; init; } = [];
}

/// <summary>
/// API response model for a license entitlement.
/// </summary>
public sealed class EntitlementResponse
{
    /// <summary>
    /// Entitlement key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable entitlement name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether this entitlement is currently active.
    /// </summary>
    public bool IsActive { get; init; }
}
