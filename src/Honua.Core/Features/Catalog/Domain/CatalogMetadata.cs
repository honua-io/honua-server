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
    /// Claim type used to resolve tenant identifiers (default: tenant_id).
    /// </summary>
    public string? TenantClaimType { get; init; } = "tenant_id";

    /// <summary>
    /// Allowed tenant identifiers for access (case-insensitive).
    /// </summary>
    public string[]? AllowedTenants { get; init; }

    /// <summary>
    /// Allowed role names for access (case-insensitive).
    /// </summary>
    public string[]? AllowedRoles { get; init; }
}
