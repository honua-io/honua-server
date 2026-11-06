// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Crs;

public static partial class CrsLinearUnitFactor
{
    public const double UsSurveyFootMeters = 1200d / 3937d;

    public const double InternationalFootMeters = 0.3048d;

    private const double IndianFootMeters = 0.3047995102481469d;

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

        if ((isGeographic ? AngularUnitFromWkt(wkt) : LinearUnitFromWkt(wkt)) is { } fromWkt)
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

    // The unit match is not anchored at the end of the WKT, so a trailing AUTHORITY clause does not hide the unit.
    public static double? LinearUnitFromWkt(string? wkt) =>
        string.IsNullOrWhiteSpace(wkt) ? null : ParseUnitFactor(LinearUnitPattern().Matches(wkt).LastOrDefault());

    private static double? AngularUnitFromWkt(string? wkt) =>
        string.IsNullOrWhiteSpace(wkt) ? null : ParseUnitFactor(AngularUnitPattern().Match(wkt));

    private static double? ParseUnitFactor(Match? match) =>
        match is { Success: true }
        && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)
        && factor > 0
        && double.IsFinite(factor)
            ? factor
            : null;

    [GeneratedRegex(@"[+]to_meter=([0-9.eE+-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ToMeterPattern();

    [GeneratedRegex(@"[+]units=([A-Za-z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UnitsPattern();

    // WKT2 names each unit kind (ANGLEUNIT, LENGTHUNIT, SCALEUNIT) and WKT1 writes a bare UNIT, so
    // each pattern accepts its own kind or a bare UNIT, never UNIT as the tail of another keyword.
    [GeneratedRegex(@"(?<![A-Za-z])(?:LENGTHUNIT|UNIT)\s*\[[^,\]]+,\s*([0-9.eE+-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinearUnitPattern();

    [GeneratedRegex(@"(?<![A-Za-z])(?:ANGLEUNIT|UNIT)\s*\[[^,\]]+,\s*([0-9.eE+-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AngularUnitPattern();
}
