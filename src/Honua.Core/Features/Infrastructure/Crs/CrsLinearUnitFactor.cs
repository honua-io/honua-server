// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// Resolves the CRS linear-unit conversion factor from PROJ and WKT text.
/// </summary>
/// <remarks>
/// Projected CRS: the value is metres per native unit. Geographic CRS: the value is
/// radians per native angular unit (the WKT <c>UNIT</c> factor, π/180 for degrees).
/// Preference is <c>+to_meter</c>, then <c>+units</c>, then the outermost linear
/// <c>UNIT</c> / <c>LENGTHUNIT</c>. A projected WKT uses the last unit. A geographic
/// WKT uses the first. The match is not anchored at the end of the string, so a
/// trailing <c>AUTHORITY</c> does not hide the unit.
/// </remarks>
public static partial class CrsLinearUnitFactor
{
    /// <summary>US survey foot, 1200/3937 metres.</summary>
    public const double UsSurveyFootMeters = 1200d / 3937d;

    /// <summary>International foot, 0.3048 metres.</summary>
    public const double InternationalFootMeters = 0.3048d;

    private const double IndianFootMeters = 0.3047995102481469d;

    /// <summary>
    /// Resolves the unit factor. Unknown text falls back to radians-per-degree for a
    /// geographic CRS and to 1 metre for a projected CRS.
    /// </summary>
    /// <param name="proj4Text">PROJ.4 definition, or null.</param>
    /// <param name="wkt">WKT1 or WKT2 definition, or null.</param>
    /// <param name="isGeographic">True when the CRS is geographic.</param>
    /// <returns>Metres per unit, or radians per angular unit when <paramref name="isGeographic"/> is true.</returns>
    public static double Resolve(string? proj4Text, string? wkt, bool isGeographic)
    {
        if (!isGeographic && TryToMeter(proj4Text, out var metersPerUnit))
        {
            return metersPerUnit;
        }

        if (TryUnits(proj4Text, isGeographic, out var fromUnits))
        {
            return fromUnits;
        }

        if (TryWkt(wkt, isGeographic, out var fromWkt))
        {
            return fromWkt;
        }

        return isGeographic ? Math.PI / 180d : 1d;
    }

    private static bool TryToMeter(string? proj4Text, out double metersPerUnit)
    {
        metersPerUnit = 0;
        if (string.IsNullOrWhiteSpace(proj4Text))
        {
            return false;
        }

        var match = ToMeterPattern().Match(proj4Text);
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out metersPerUnit)
            && metersPerUnit > 0
            && double.IsFinite(metersPerUnit);
    }

    private static bool TryUnits(string? proj4Text, bool isGeographic, out double factor)
    {
        factor = 0;
        if (string.IsNullOrWhiteSpace(proj4Text))
        {
            return false;
        }

        var match = UnitsPattern().Match(proj4Text);
        if (!match.Success)
        {
            return false;
        }

        var token = match.Groups[1].Value;
        if (isGeographic)
        {
            if (token.Equals("deg", StringComparison.OrdinalIgnoreCase)
                || token.Equals("degree", StringComparison.OrdinalIgnoreCase))
            {
                factor = Math.PI / 180d;
                return true;
            }

            if (token.Equals("rad", StringComparison.OrdinalIgnoreCase)
                || token.Equals("radian", StringComparison.OrdinalIgnoreCase))
            {
                factor = 1d;
                return true;
            }

            return false;
        }

        if (token.Equals("m", StringComparison.OrdinalIgnoreCase)
            || token.Equals("meter", StringComparison.OrdinalIgnoreCase)
            || token.Equals("metre", StringComparison.OrdinalIgnoreCase))
        {
            factor = 1d;
            return true;
        }

        if (token.Equals("us-ft", StringComparison.OrdinalIgnoreCase)
            || token.Equals("ftus", StringComparison.OrdinalIgnoreCase))
        {
            factor = UsSurveyFootMeters;
            return true;
        }

        if (token.Equals("ft", StringComparison.OrdinalIgnoreCase)
            || token.Equals("foot", StringComparison.OrdinalIgnoreCase))
        {
            factor = InternationalFootMeters;
            return true;
        }

        if (token.Equals("ind-ft", StringComparison.OrdinalIgnoreCase))
        {
            factor = IndianFootMeters;
            return true;
        }

        return false;
    }

    private static bool TryWkt(string? wkt, bool isGeographic, out double factor)
    {
        factor = 0;
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return false;
        }

        var matches = UnitPattern().Matches(wkt);
        if (matches.Count == 0)
        {
            return false;
        }

        var match = isGeographic ? matches[0] : matches[^1];
        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out factor)
            && factor > 0
            && double.IsFinite(factor);
    }

    [GeneratedRegex(@"[+]to_meter=([0-9.eE+-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ToMeterPattern();

    [GeneratedRegex(@"[+]units=([A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UnitsPattern();

    // ANGLEUNIT contains UNIT. The lookbehind keeps the angular name from being read as linear.
    [GeneratedRegex(@"(?<!ANGLE)(?:LENGTHUNIT|UNIT)\s*\[[^,\]]+,\s*([0-9.eE+-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern();
}
