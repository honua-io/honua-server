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
        2265 or 2266 => InternationalFootMeters,
        2269 or 2270 => InternationalFootMeters,
        2273 => InternationalFootMeters,
        2868 or 2869 or 2870 => InternationalFootMeters,
        2896 or 2897 or 2898 => InternationalFootMeters,
        2901 => InternationalFootMeters,
        2909 or 2910 => InternationalFootMeters,
        2913 or 2914 or 2915 => InternationalFootMeters,
        6404 or 6405 or 6406 => InternationalFootMeters,
        6497 or 6498 or 6499 or 6501 => InternationalFootMeters,
        6529 or 6531 => InternationalFootMeters,
        6557 or 6559 => InternationalFootMeters,
        6570 => InternationalFootMeters,

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
        // NAD83(2011) State Plane zones in US survey feet (explicit ftUS codes; metre codes in 6400..6600 fall through to 1.0)
        6414 or 6416 or 6418 or 6420 or 6422 or 6424 or 6426 or 6428 or 6430 or 6432 or 6434 or 6436
            or 6438 or 6441 or 6443 or 6445 or 6447 or 6449 or 6451 or 6453 or 6455 or 6457 or 6459
            or 6461 or 6463 or 6465 or 6467 or 6469 or 6471 or 6473 or 6475 or 6477 or 6479 or 6484
            or 6486 or 6488 or 6492 or 6494 or 6503 or 6505 or 6507 or 6509 or 6511 or 6513 or 6515
            or 6517 or 6521 or 6523 or 6525 or 6527 or 6533 or 6535 or 6537 or 6539 or 6541 or 6543
            or 6545 or 6547 or 6549 or 6551 or 6553 or 6555 or 6561 or 6563 or 6565 or 6568 or 6572
            or 6574 or 6576 or 6578 or 6580 or 6582 or 6584 or 6586 or 6588 or 6590 or 6593 or 6595
            or 6597 or 6599 or 6601 or 6603 or 6605 or 6607 or 6609 => UsSurveyFootMeters,

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
                if (TryLocateWktNode(current, keyword, out var range) && range.Start < bestStart)
                {
                    bestStart = range.Start;
                    bestMember = current.Substring(range.Start, range.End - range.Start + 1);
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
            if (TryLocateWktNode(wkt, keyword, out var range) && range.End > maxEnd)
            {
                maxEnd = range.End;
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
        if (TryLocateWktNode(wkt, keyword, out var range))
        {
            member = wkt.Substring(range.Start, range.End - range.Start + 1);
            return true;
        }

        member = string.Empty;
        return false;
    }

    public static bool TryLocateWktNode(string wkt, string keyword, out (int Start, int End) span)
    {
        span = (-1, -1);
        for (var idx = wkt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
             idx >= 0;
             idx = wkt.IndexOf(keyword, idx + keyword.Length, StringComparison.OrdinalIgnoreCase))
        {
            if (idx > 0 && (char.IsLetterOrDigit(wkt[idx - 1]) || wkt[idx - 1] == '_'))
            {
                continue;
            }

            var cursor = idx + keyword.Length;
            while (cursor < wkt.Length && char.IsWhiteSpace(wkt[cursor]))
            {
                cursor++;
            }

            if (cursor >= wkt.Length || wkt[cursor] != '[')
            {
                continue;
            }

            var bracketDepth = 0;
            var insideQuotes = false;
            for (var pos = cursor; pos < wkt.Length; pos++)
            {
                var ch = wkt[pos];
                if (ch == '"')
                {
                    insideQuotes = !insideQuotes;
                }
                else if (!insideQuotes && ch == '[')
                {
                    bracketDepth++;
                }
                else if (!insideQuotes && ch == ']' && --bracketDepth == 0)
                {
                    span = (idx, pos);
                    return true;
                }
            }
        }

        return false;
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
