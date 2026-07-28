// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Shared normalization for the local geocoder's <c>search_text</c> reference column. The same
/// normalization must be applied when reference records are loaded (for example by the Esri
/// locator import, #2152) and when queries are matched at request time (#2151), so both paths
/// share this helper.
/// </summary>
internal static partial class GeocodeReferenceText
{
    /// <summary>
    /// Normalizes to the documented <c>search_text</c> form: lowercase, trimmed, single-spaced,
    /// with separator punctuation (commas/semicolons) canonicalized away. Applying the same rule
    /// to loaded records and incoming queries keeps a comma-formatted display address
    /// ("380 New York St, Redlands") and the provider's space-joined structured composition
    /// ("380 New York St Redlands") equal after normalization.
    /// </summary>
    public static string Normalize(string text)
        => WhitespaceRegex().Replace(
            SeparatorRegex().Replace(text.ToLowerInvariant(), " ").Trim(), " ");

    [GeneratedRegex(@"[,;]")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
