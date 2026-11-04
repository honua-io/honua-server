// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public static class GeocodeLocatorNameRules
{
    public const string ValidationMessage = "Locator name must not be '.', '..', or contain '/' or control characters.";

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
