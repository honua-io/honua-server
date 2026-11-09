// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

// Direct equality on floating-point values is unreliable after arithmetic, a unit conversion, or a cross-precision cast.
public static class NumericTolerance
{
    public const double DefaultEpsilon = 1e-9;

    public static bool NearlyEqual(double left, double right, double epsilon = DefaultEpsilon)
        => Math.Abs(left - right) <= epsilon;

    public static bool IsEffectivelyZero(double value, double epsilon = DefaultEpsilon)
        => Math.Abs(value) <= epsilon;

    public static bool IsWholeNumber(double value, double epsilon = DefaultEpsilon)
        => Math.Abs(value - Math.Round(value)) <= epsilon;
}
