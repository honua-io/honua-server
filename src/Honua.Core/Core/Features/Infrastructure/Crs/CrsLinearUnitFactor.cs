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

    public static double LinearUnitToMeters(int srid) => srid switch
    {
        // NAD83 / NAD83(HARN) / NAD83(2011) State Plane zones using International Feet (0.3048 m)
        2222 or 2223 or 2224 => InternationalFootMeters,
        2251 or 2252 or 2253 => InternationalFootMeters,
        2257 => InternationalFootMeters,
        2269 or 2270 => InternationalFootMeters,
        2273 => InternationalFootMeters,
        2868 or 2869 or 2870 => InternationalFootMeters,
        2896 or 2897 or 2898 => InternationalFootMeters,
        2901 => InternationalFootMeters,
        2913 or 2914 or 2915 => InternationalFootMeters,
        6404 or 6405 or 6406 => InternationalFootMeters,

        // NAD83 State Plane zones in US survey feet (1200/3937 m)
        >= 2225 and <= 2286 => UsSurveyFootMeters,
        // NAD83(HARN) State Plane zones in US survey feet
        >= 2867 and <= 2930 => UsSurveyFootMeters,
        // NAD83 / Indiana East & West (ftUS)
        2965 or 2966 => UsSurveyFootMeters,
        // NAD83 / Arkansas, Illinois, New Hampshire, Rhode Island (ftUS)
        >= 3433 and <= 3438 => UsSurveyFootMeters,
        // NAD83(NSRS2007) State Plane in US survey feet
        >= 3451 and <= 3466 => UsSurveyFootMeters,
        // NAD83(2011) State Plane in US survey feet
        >= 6425 and <= 6599 => UsSurveyFootMeters,

        // All other projected CRSs default to meters
        _ => 1d
    };

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
    public static double? LinearUnitFromWkt(string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        var scoped = ScopeToHorizontalCrs(wkt);
        if (IsGeographicWkt(scoped))
        {
            return ParseUnitFactor(AngularUnitPattern().Matches(scoped).LastOrDefault());
        }

        var outerWkt = StripBaseGeographicBlockIfProjected(scoped);
        return ParseUnitFactor(LinearUnitPattern().Matches(outerWkt).LastOrDefault());
    }

    private static double? AngularUnitFromWkt(string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        var scoped = ScopeToHorizontalCrs(wkt);
        return ParseUnitFactor(AngularUnitPattern().Matches(scoped).LastOrDefault());
    }

    private static readonly string[] _horizontalCrsKeywords =
    [
        "PROJCRS",
        "PROJECTEDCRS",
        "PROJCS",
        "GEOGCRS",
        "GEOGRAPHICCRS",
        "GEODCRS",
        "GEOGCS"
    ];

    private static readonly string[] _projectedCrsKeywords =
    [
        "PROJCRS",
        "PROJECTEDCRS",
        "PROJCS"
    ];

    private static readonly string[] _baseGeographicKeywords =
    [
        "BASEGEOGCRS",
        "BASEGEODCRS",
        "GEOGCS",
        "GEODCRS",
        "GEOGRAPHICCRS",
        "GEOGCRS"
    ];

    private static string ScopeToHorizontalCrs(string wkt)
    {
        var current = wkt.Trim();
        if (StartsWithKeyword(current, "BOUNDCRS") &&
            TryExtractMember(current, "SOURCECRS", out var sourceCrs))
        {
            current = UnwrapContainer(sourceCrs, "SOURCECRS");
        }

        if (StartsWithKeyword(current, "COMPD_CS") || StartsWithKeyword(current, "COMPOUNDCRS"))
        {
            var bestStart = int.MaxValue;
            string? bestMember = null;
            foreach (var keyword in _horizontalCrsKeywords)
            {
                if (TryFindMember(current, keyword, out var start, out var end) && start < bestStart)
                {
                    bestStart = start;
                    bestMember = current.Substring(start, end - start + 1);
                }
            }

            if (bestMember is not null)
            {
                return bestMember;
            }
        }

        return current;
    }

    private static bool IsGeographicWkt(string wkt)
    {
        if (StartsWithKeyword(wkt, "GEOGCRS") ||
            StartsWithKeyword(wkt, "GEOGRAPHICCRS") ||
            StartsWithKeyword(wkt, "GEOGCS"))
        {
            return true;
        }

        if (StartsWithKeyword(wkt, "GEODCRS"))
        {
            return !wkt.Contains("CS[Cartesian", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string StripBaseGeographicBlockIfProjected(string wkt)
    {
        var isProjected = false;
        foreach (var keyword in _projectedCrsKeywords)
        {
            if (StartsWithKeyword(wkt, keyword))
            {
                isProjected = true;
                break;
            }
        }

        if (!isProjected)
        {
            return wkt;
        }

        var maxEnd = -1;
        foreach (var keyword in _baseGeographicKeywords)
        {
            if (TryFindMember(wkt, keyword, out _, out var end) && end > maxEnd)
            {
                maxEnd = end;
            }
        }

        return maxEnd >= 0 && maxEnd + 1 < wkt.Length ? wkt[(maxEnd + 1)..] : wkt;
    }

    private static string UnwrapContainer(string wkt, string keyword)
    {
        var trimmed = wkt.Trim();
        if (!StartsWithKeyword(trimmed, keyword))
        {
            return trimmed;
        }

        var open = trimmed.IndexOf('[');
        if (open < 0 || trimmed[^1] != ']')
        {
            return trimmed;
        }

        return trimmed[(open + 1)..^1].Trim();
    }

    private static bool StartsWithKeyword(string wkt, string keyword)
    {
        var span = wkt.AsSpan().TrimStart();
        if (!span.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = span[keyword.Length..].TrimStart();
        return rest.Length > 0 && rest[0] == '[';
    }

    private static bool TryExtractMember(string wkt, string keyword, out string member)
    {
        if (TryFindMember(wkt, keyword, out var start, out var end))
        {
            member = wkt.Substring(start, end - start + 1);
            return true;
        }

        member = string.Empty;
        return false;
    }

    private static bool TryFindMember(string wkt, string keyword, out int start, out int end)
    {
        start = -1;
        end = -1;

        var idx = wkt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var isWordStart = idx == 0 || (!char.IsLetterOrDigit(wkt[idx - 1]) && wkt[idx - 1] != '_');
            var i = idx + keyword.Length;
            while (i < wkt.Length && char.IsWhiteSpace(wkt[i]))
            {
                i++;
            }

            if (isWordStart && i < wkt.Length && wkt[i] == '[')
            {
                var close = FindMatchingBracket(wkt, i);
                if (close > i)
                {
                    start = idx;
                    end = close;
                    return true;
                }
            }

            idx = wkt.IndexOf(keyword, idx + keyword.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int FindMatchingBracket(string s, int openIndex)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = openIndex; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                continue;
            }

            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

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
