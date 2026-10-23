// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>Shared route-segment validation for configured locator names.</summary>
public static class GeocodeLocatorNameRules
{
    /// <summary>Describes locator names that cannot be addressed by the named route.</summary>
    public const string ValidationMessage = "Locator name must not be '.', '..', or contain '/' or control characters.";

    /// <summary>Whether a configured locator name can occupy a named route segment.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character == '/' || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
