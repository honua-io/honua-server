// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Optional catalog metadata for services and layers.
/// </summary>
public sealed record CatalogMetadata
{
    /// <summary>
    /// Authorization policy for accessing the catalog resource.
    /// </summary>
    public AccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// Temporal metadata for time-aware layers.
    /// </summary>
    public LayerTimeInfo? TimeInfo { get; init; }
}

/// <summary>
/// Authorization policy for catalog resources (services/layers).
/// </summary>
public sealed record AccessPolicy
{
    /// <summary>
    /// When true, anonymous access is allowed regardless of other constraints.
    /// </summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// When true, anonymous write access is allowed regardless of other constraints.
    /// </summary>
    public bool AllowAnonymousWrite { get; init; }

    /// <summary>
    /// Claim type used to resolve tenant identifiers (default: tenant_id).
    /// </summary>
    public string? TenantClaimType { get; init; } = "tenant_id";

    /// <summary>
    /// Allowed tenant identifiers for access (case-insensitive).
    /// </summary>
    public string[]? AllowedTenants { get; init; }

    /// <summary>
    /// Allowed tenant identifiers for write access (case-insensitive).
    /// Falls back to AllowedTenants when not specified.
    /// </summary>
    public string[]? AllowedWriteTenants { get; init; }

    /// <summary>
    /// Allowed role names for access (case-insensitive).
    /// </summary>
    public string[]? AllowedRoles { get; init; }

    /// <summary>
    /// Allowed role names for write access (case-insensitive).
    /// Falls back to AllowedRoles when not specified.
    /// </summary>
    public string[]? AllowedWriteRoles { get; init; }
}

/// <summary>
/// Temporal metadata for layers with time awareness.
/// </summary>
public sealed record LayerTimeInfo
{
    /// <summary>
    /// Field name containing the start time (required when time info is present).
    /// </summary>
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Field name containing the end time (optional for interval data).
    /// </summary>
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Optional track identifier field for temporal visualization.
    /// </summary>
    public string? TrackIdField { get; init; }
}
