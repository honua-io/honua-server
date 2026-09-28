// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>Validates locator names before they are projected into a URL path segment.</summary>
public static class GeocodeLocatorNameSyntax
{
    /// <summary>
    /// Returns whether <paramref name="name"/> can occupy exactly one URL route segment.
    /// Unicode and literal percent characters remain valid because URL generation escapes them.
    /// </summary>
    public static bool IsRouteSafe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        return name.All(static character => !char.IsControl(character) && character is not '/' and not '\\');
    }
}
