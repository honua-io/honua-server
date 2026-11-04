// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Validation;

/// <summary>
/// The ordinates of a comma-separated bounding-box parameter: four (2D) or six (3D) finite numbers in
/// invariant culture. Each protocol applies its own ordering, range and CRS rules to the result.
/// </summary>
public static class BoundingBoxOrdinates
{
    /// <summary>Parses <paramref name="text"/> as four or six comma-separated finite numbers.</summary>
    /// <param name="text">The parameter value, or null.</param>
    /// <param name="ordinates">The parsed ordinates; empty when parsing fails.</param>
    /// <returns>True when the value holds exactly four or six finite numbers.</returns>
    public static bool TryParse(string? text, out double[] ordinates)
        => TryParse((text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries), out ordinates);

    /// <summary>Parses already-split tokens as four or six finite numbers.</summary>
    /// <param name="tokens">The comma-separated tokens.</param>
    /// <param name="ordinates">The parsed ordinates; empty when parsing fails.</param>
    /// <returns>True when there are exactly four or six tokens and every one is a finite number.</returns>
    public static bool TryParse(IReadOnlyList<string> tokens, out double[] ordinates)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ordinates = [];
        if (tokens.Count is not (4 or 6))
        {
            return false;
        }

        var parsed = new double[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) ||
                !double.IsFinite(parsed[i]))
            {
                return false;
            }
        }

        ordinates = parsed;
        return true;
    }

    /// <summary>
    /// The horizontal corners. A six-value box lists the minimum Z after the minimum corner and the
    /// maximum Z after the maximum corner.
    /// </summary>
    /// <param name="ordinates">Four or six ordinates from <see cref="TryParse(string?, out double[])"/>.</param>
    /// <returns>The minimum and maximum horizontal ordinates.</returns>
    public static (double MinX, double MinY, double MaxX, double MaxY) Horizontal(IReadOnlyList<double> ordinates)
    {
        ArgumentNullException.ThrowIfNull(ordinates);
        return ordinates.Count == 6
            ? (ordinates[0], ordinates[1], ordinates[3], ordinates[4])
            : (ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
    }
}
