// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization;

namespace Honua.Core.Features.Authorization.Domain;

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
    public static string ToWireName(this PortalAccessLevel level) => level switch
    {
        PortalAccessLevel.Public => "public",
        PortalAccessLevel.Organization => "org",
        _ => "private",
    };
}
