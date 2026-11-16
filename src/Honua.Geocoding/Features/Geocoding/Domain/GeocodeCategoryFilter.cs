// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public static class GeocodeCategoryFilter
{
    private static readonly char[] TokenSeparators = [',', ';'];

    public static IReadOnlyList<string>? ParseCategories(string? rawCategory)
    {
        if (string.IsNullOrWhiteSpace(rawCategory))
        {
            return null;
        }

        var tokens = rawCategory.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? null : tokens;
    }

    public static bool Matches(string? categoryValue, IReadOnlyList<string>? requestedCategories)
    {
        if (requestedCategories is null || requestedCategories.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(categoryValue))
        {
            // A result with no category data cannot satisfy an explicit category filter.
            return false;
        }

        return requestedCategories.Any(requested => CategoryEquals(categoryValue, requested));
    }

    private static bool CategoryEquals(string value, string requested)
    {
        if (string.Equals(value, requested, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Esri groups the point/street/subaddress family under the "Address" category. Recognise
        // that grouping so a client filtering on category=Address still receives address-typed
        // results that providers label more specifically (PointAddress, StreetAddress, ...).
        if (string.Equals(requested, "Address", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith("Address", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
