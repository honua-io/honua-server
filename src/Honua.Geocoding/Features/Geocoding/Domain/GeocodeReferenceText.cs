// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Geocoding.Features.Geocoding.Domain;

// Import and query must share this normalization, or a stored search_text will not match the text submitted at request time.
internal static partial class GeocodeReferenceText
{
    public static string Normalize(string text)
        => WhitespaceRegex().Replace(
            SeparatorRegex().Replace(text.ToLowerInvariant(), " ").Trim(), " ");

    [GeneratedRegex(@"[,;]")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
