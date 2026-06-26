// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Testing;

/// <summary>
/// Tolerances applied when a <see cref="GpProcessTestRunner"/> compares an artifact
/// against a golden file (GP Devkit P6, issue #2127). Golden GP tests assert
/// equivalence — not byte-equality — because geometry algorithms (buffer segmentation,
/// reprojection, GDAL number formatting) legitimately vary in the least-significant
/// digits across platforms and library versions. The tolerance is what makes a golden
/// stable across those without masking a real regression.
/// </summary>
/// <param name="Coordinate">
/// Maximum allowed absolute difference, per ordinate, between a coordinate in the actual
/// geometry and the aligned coordinate in the golden geometry (in the geometry's own CRS
/// units). Used by the geometry comparator. Must be non-negative.
/// </param>
/// <param name="Numeric">
/// Maximum allowed absolute difference between a numeric leaf in the actual output and the
/// aligned numeric leaf in the golden output. Used by the scalar/structural comparator for
/// JSON numbers and parsed numeric cells (e.g. raster statistics, CSV measures). Must be
/// non-negative.
/// </param>
public readonly record struct GoldenTolerance(double Coordinate, double Numeric)
{
    /// <summary>
    /// The default tolerance: <c>1e-6</c> on both coordinates and numbers. Tight enough to
    /// catch a genuine geometry/value change, loose enough to absorb floating-point and
    /// formatting noise.
    /// </summary>
    public static GoldenTolerance Default { get; } = new(Coordinate: 1e-6, Numeric: 1e-6);

    /// <summary>
    /// Builds a tolerance, validating that neither component is negative or NaN.
    /// </summary>
    /// <param name="coordinate">Per-ordinate coordinate tolerance.</param>
    /// <param name="numeric">Numeric leaf tolerance.</param>
    /// <returns>The validated tolerance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A component is negative or NaN.</exception>
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
