// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Benchmarks.RasterStorage;

internal sealed record RasterGridAlignmentResult(bool IsAligned, IReadOnlyList<string> Issues);

internal static class RasterGridAlignment
{
    private const double RelativeTolerance = 1e-9;

    public static RasterGridAlignmentResult Analyze(IReadOnlyList<RasterGrid> grids)
    {
        ArgumentNullException.ThrowIfNull(grids);
        if (grids.Count == 0)
        {
            return new RasterGridAlignmentResult(false, ["The collection contains no raster grids."]);
        }

        var reference = grids[0];
        var issues = new List<string>();
        ValidateGrid(reference, 0, issues);
        for (var index = 1; index < grids.Count; index++)
        {
            var candidate = grids[index];
            ValidateGrid(candidate, index, issues);
            if (candidate.Srid != reference.Srid)
            {
                issues.Add($"Scene {index} SRID {candidate.Srid} does not match {reference.Srid}.");
            }

            CompareTransform("scaleX", reference.ScaleX, candidate.ScaleX, index, issues);
            CompareTransform("scaleY", reference.ScaleY, candidate.ScaleY, index, issues);
            CompareTransform("skewX", reference.SkewX, candidate.SkewX, index, issues);
            CompareTransform("skewY", reference.SkewY, candidate.SkewY, index, issues);

            if (!SharesLattice(reference.OriginX, candidate.OriginX, reference.ScaleX))
            {
                issues.Add($"Scene {index} X origin is not on the reference pixel lattice.");
            }

            if (!SharesLattice(reference.OriginY, candidate.OriginY, reference.ScaleY))
            {
                issues.Add($"Scene {index} Y origin is not on the reference pixel lattice.");
            }
        }

        return new RasterGridAlignmentResult(issues.Count == 0, issues);
    }

    private static void ValidateGrid(RasterGrid grid, int index, List<string> issues)
    {
        if (grid.Width <= 0 || grid.Height <= 0)
        {
            issues.Add($"Scene {index} has non-positive dimensions.");
        }

        if (!double.IsFinite(grid.ScaleX) || !double.IsFinite(grid.ScaleY) ||
            grid.ScaleX == 0 || grid.ScaleY == 0)
        {
            issues.Add($"Scene {index} has an invalid pixel scale.");
        }
    }

    private static void CompareTransform(
        string name,
        double expected,
        double actual,
        int index,
        List<string> issues)
    {
        var tolerance = Math.Max(Math.Abs(expected), 1) * RelativeTolerance;
        if (Math.Abs(expected - actual) > tolerance)
        {
            issues.Add($"Scene {index} {name} {actual:R} does not match {expected:R}.");
        }
    }

    private static bool SharesLattice(double referenceOrigin, double candidateOrigin, double scale)
    {
        if (!double.IsFinite(referenceOrigin) || !double.IsFinite(candidateOrigin) ||
            !double.IsFinite(scale) || scale == 0)
        {
            return false;
        }

        var pixelOffset = (candidateOrigin - referenceOrigin) / scale;
        var nearestPixel = Math.Round(pixelOffset);
        return Math.Abs(pixelOffset - nearestPixel) <= RelativeTolerance * Math.Max(Math.Abs(pixelOffset), 1);
    }
}
