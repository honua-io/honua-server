// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// The ArcGIS Portal/Sharing notion of item and service <c>access</c> (#1370).
/// This is a projection of the canonical RBAC decision
/// (<see cref="PermissionDecision"/>) and the coarse
/// <c>AccessPolicy</c> seam onto the three values Esri clients expect from a
/// Portal facade; it is <b>not</b> a new access-control model.
/// </summary>
/// <remarks>
/// The string forms (<see cref="PortalAccessLevelExtensions.ToWireName"/>) are
/// the exact lowercase tokens ArcGIS Pro / Field Maps expect in
/// <c>content/items/{id}</c> and <c>search</c> responses. The shared
/// <c>IPortalAccessProjection</c> is the single helper the Portal read surface
/// (#1243) and the OAuth2 bridge (#1242) consume so the reported access never
/// drifts from the real authorization decision.
/// </remarks>
public enum PortalAccessLevel
{
    /// <summary>
    /// Visible only to the owner / explicitly granted principals; anonymous and
    /// general authenticated users cannot see or open the item (wire name
    /// <c>"private"</c>).
    /// </summary>
    Private = 0,

    /// <summary>
    /// Visible to authenticated members of the organization (any signed-in
    /// principal that the policy/grant authorizes), but not to anonymous callers
    /// (wire name <c>"org"</c>).
    /// </summary>
    Organization = 1,

    /// <summary>
    /// Visible to everyone, including anonymous callers (wire name
    /// <c>"public"</c>).
    /// </summary>
    Public = 2,
}

/// <summary>
/// Wire-name helpers for <see cref="PortalAccessLevel"/>.
/// </summary>
public static class PortalAccessLevelExtensions
{
    /// <summary>
    /// Returns the lowercase ArcGIS Portal wire token for an access level
    /// (<c>"private"</c>, <c>"org"</c>, or <c>"public"</c>).
    /// </summary>
    /// <param name="level">The access level.</param>
    /// <returns>The Esri-compatible wire token.</returns>
    public static string ToWireName(this PortalAccessLevel level) => level switch
    {
        PortalAccessLevel.Public => "public",
        PortalAccessLevel.Organization => "org",
        _ => "private",
    };
}
