// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Validation;

public readonly struct BoundingBoxParse
{
    public bool Succeeded { get; }

    public double[] Ordinates { get; }

    private BoundingBoxParse(bool succeeded, double[] ordinates)
    {
        Succeeded = succeeded;
        Ordinates = ordinates;
    }

    public static BoundingBoxParse Success(double[] ordinates) => new(true, ordinates);

    public static BoundingBoxParse Failed() => new(false, []);
}

// Four or six finite numbers in invariant culture. A six-value box is
// minx, miny, minz, maxx, maxy, maxz. Each caller applies its own range and CRS rules.
public static class BoundingBoxOrdinates
{
    public static BoundingBoxParse Parse(string? text) =>
        Parse((text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries));

    public static BoundingBoxParse Parse(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count is not (4 or 6))
        {
            return BoundingBoxParse.Failed();
        }

        var parsed = new double[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) ||
                !double.IsFinite(parsed[i]))
            {
                return BoundingBoxParse.Failed();
            }
        }

        return BoundingBoxParse.Success(parsed);
    }

    public static (double MinX, double MinY, double MaxX, double MaxY) Horizontal(IReadOnlyList<double> ordinates)
    {
        ArgumentNullException.ThrowIfNull(ordinates);
        if (ordinates.Count is not (4 or 6))
        {
            throw new ArgumentOutOfRangeException(nameof(ordinates), "Expected four or six ordinates.");
        }

        // Minimum Z sits at index 2 and maximum Z at index 5, between the horizontal corners.
        return ordinates.Count == 6
            ? (ordinates[0], ordinates[1], ordinates[3], ordinates[4])
            : (ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
    }
}
