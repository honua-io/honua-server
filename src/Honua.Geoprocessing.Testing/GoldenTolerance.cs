// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing.Testing;

// Goldens allow a small numeric tolerance because geometry and GDAL formatting legitimately differ in the last digits across platforms.
public readonly record struct GoldenTolerance(double Coordinate, double Numeric)
{
    public static GoldenTolerance Default { get; } = new(Coordinate: 1e-6, Numeric: 1e-6);

    public static GoldenTolerance Create(double coordinate, double numeric)
    {
        if (double.IsNaN(coordinate) || coordinate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coordinate), coordinate, "Tolerance must be a non-negative number.");
        }

        if (double.IsNaN(numeric) || numeric < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numeric), numeric, "Tolerance must be a non-negative number.");
        }

        return new GoldenTolerance(coordinate, numeric);
    }
}
