// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Shared tolerance-based comparison helpers for floating-point values. Direct
/// <c>==</c>/<c>!=</c> comparisons on <see cref="double"/>/<see cref="float"/> become
/// unreliable once a value has passed through arithmetic, a unit conversion, or a
/// cross-precision cast. Protocol adapters and rendering/geometry code should reuse
/// these helpers instead of duplicating a local epsilon.
/// </summary>
public static class NumericTolerance
{
    /// <summary>
    /// Default absolute epsilon used when no scale-specific tolerance is supplied.
    /// Suitable for degenerate-value/near-zero guards (e.g. divide-by-zero checks) and
    /// whole-number/coordinate comparisons at typical geospatial and request-parameter
    /// magnitudes.
    /// </summary>
    public const double DefaultEpsilon = 1e-9;

    /// <summary>
    /// Returns <see langword="true"/> when two values are equal within <paramref name="epsilon"/>.
    /// </summary>
    public static bool NearlyEqual(double left, double right, double epsilon = DefaultEpsilon)
        => Math.Abs(left - right) <= epsilon;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is close enough to zero
    /// to be treated as a degenerate divisor/length/scale. Use this instead of <c>value == 0</c>
    /// to guard divide-by-zero and divide-by-near-zero blow-ups on computed or user-supplied
    /// doubles.
    /// </summary>
    public static bool IsEffectivelyZero(double value, double epsilon = DefaultEpsilon)
        => Math.Abs(value) <= epsilon;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is within
    /// <paramref name="epsilon"/> of a whole number.
    /// </summary>
    public static bool IsWholeNumber(double value, double epsilon = DefaultEpsilon)
        => Math.Abs(value - Math.Round(value)) <= epsilon;
}
