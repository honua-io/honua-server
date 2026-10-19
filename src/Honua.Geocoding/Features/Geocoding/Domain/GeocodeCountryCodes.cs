// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Parses the advertised comma-separated, two-letter geocoding country filter.
/// </summary>
public static class GeocodeCountryCodes
{
    /// <summary>The client error returned for a malformed country filter.</summary>
    public const string InvalidMessage = "Invalid countryCodes: expected comma-separated two-letter ISO country codes.";

    /// <summary>
    /// Parses country codes without accepting an empty entry or a non-ASCII code.
    /// A missing or whitespace-only filter applies no country restriction.
    /// </summary>
    /// <param name="value">The requested comma-separated country codes.</param>
    /// <param name="codes">Distinct uppercase codes, or null for no filter.</param>
    /// <returns>True when the filter has the advertised shape.</returns>
    public static bool TryParse(string? value, out string[]? codes)
    {
        codes = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Every ISO alpha-2 country fits well inside this bound. Check before
        // splitting so a hostile request cannot allocate an unbounded array.
        if (value.Length > 1024)
        {
            return false;
        }

        var requested = value.Split(',', StringSplitOptions.TrimEntries);
        if (requested.Length > 250 || requested.Any(static code =>
                code.Length != 2 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1])))
        {
            return false;
        }

        codes = requested.Select(static code => code.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return true;
    }
}
