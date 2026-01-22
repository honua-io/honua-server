// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Styling;

internal static class EsriStyleMappings
{
    public const string LineStyleSolid = "esriSLSSolid";
    public const string LineStyleDash = "esriSLSDash";
    public const string LineStyleDot = "esriSLSDot";
    public const string LineStyleDashDot = "esriSLSDashDot";
    public const string LineStyleDashDotDot = "esriSLSDashDotDot";

    public const string FillStyleSolid = "esriSFSSolid";
    public const string FillStyleNull = "esriSFSNull";

    private static readonly double[] DashPattern = [4d, 2d];
    private static readonly double[] DotPattern = [1d, 2d];
    private static readonly double[] DashDotPattern = [4d, 2d, 1d, 2d];
    private static readonly double[] DashDotDotPattern = [4d, 2d, 1d, 2d, 1d, 2d];

    public static bool IsNullFillStyle(string? style)
        => string.Equals(style, FillStyleNull, StringComparison.OrdinalIgnoreCase);

    public static bool TryGetLineDashArray(string? style, out double[]? dashArray)
    {
        dashArray = null;

        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        if (string.Equals(style, LineStyleDash, StringComparison.OrdinalIgnoreCase))
        {
            dashArray = (double[])DashPattern.Clone();
            return true;
        }

        if (string.Equals(style, LineStyleDot, StringComparison.OrdinalIgnoreCase))
        {
            dashArray = (double[])DotPattern.Clone();
            return true;
        }

        if (string.Equals(style, LineStyleDashDot, StringComparison.OrdinalIgnoreCase))
        {
            dashArray = (double[])DashDotPattern.Clone();
            return true;
        }

        if (string.Equals(style, LineStyleDashDotDot, StringComparison.OrdinalIgnoreCase))
        {
            dashArray = (double[])DashDotDotPattern.Clone();
            return true;
        }

        return false;
    }

    public static bool TryGetLineStyleFromDashArray(IReadOnlyList<double> dashArray, out string? style)
    {
        style = null;

        if (dashArray.Count == 0)
        {
            return false;
        }

        if (MatchesPattern(dashArray, DashPattern))
        {
            style = LineStyleDash;
            return true;
        }

        if (MatchesPattern(dashArray, DotPattern))
        {
            style = LineStyleDot;
            return true;
        }

        if (MatchesPattern(dashArray, DashDotPattern))
        {
            style = LineStyleDashDot;
            return true;
        }

        if (MatchesPattern(dashArray, DashDotDotPattern))
        {
            style = LineStyleDashDotDot;
            return true;
        }

        return false;
    }

    private static bool MatchesPattern(IReadOnlyList<double> actual, double[] expected)
    {
        if (actual.Count != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(actual[i] - expected[i]) > 0.01d)
            {
                return false;
            }
        }

        return true;
    }
}
