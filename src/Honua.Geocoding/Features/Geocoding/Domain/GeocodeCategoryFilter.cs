// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Parses GeoServices <c>category</c> tokens and matches them against the category/address-type
/// metadata a provider returns on its candidates and suggestions.
/// </summary>
/// <remarks>
/// GeoServices <c>category</c> filtering narrows results to a set of place categories
/// (e.g. <c>Address</c>, <c>POI</c>, <c>StreetName</c>, <c>City</c>). Honua filters on the
/// category data a provider already returns — a candidate's <see cref="GeocodeCandidate.AddressType"/>
/// or a suggestion's <see cref="GeocodeSuggestion.Category"/> — rather than asking the upstream
/// API to filter (no backing provider exposes a forward category parameter). Matching is
/// case-insensitive and tolerant of the common Esri synonym/grouping where <c>Address</c> covers
/// the point/street/subaddress family, so the filter is honest about provider-supplied data while
/// still recognising the categories Esri clients send.
/// </remarks>
public static class GeocodeCategoryFilter
{
    private static readonly char[] TokenSeparators = [',', ';'];

    /// <summary>
    /// Splits a raw <c>category</c> parameter into normalized, non-empty tokens. Returns
    /// <see langword="null"/> when no category filter is requested.
    /// </summary>
    /// <param name="rawCategory">The raw comma/semicolon-delimited category parameter.</param>
    /// <returns>The requested category tokens, or <see langword="null"/> when none were supplied.</returns>
    public static IReadOnlyList<string>? ParseCategories(string? rawCategory)
    {
        if (string.IsNullOrWhiteSpace(rawCategory))
        {
            return null;
        }

        var tokens = rawCategory.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? null : tokens;
    }

    /// <summary>
    /// Determines whether a candidate/suggestion category value matches any requested category.
    /// </summary>
    /// <param name="categoryValue">The provider-supplied category or address type.</param>
    /// <param name="requestedCategories">The requested category tokens; <see langword="null"/> matches everything.</param>
    /// <returns><see langword="true"/> when no filter is requested or the value matches a requested category.</returns>
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
