// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Honua.Db.Postgres.Features.FileImport;

/// <summary>
/// Identifies an ESRI WKT1 projected CRS that has no AUTHORITY node by its projection
/// parameters, then picks the EPSG row in <c>spatial_ref_sys</c> with the same parameters
/// and datum. This is what desktop GIS tools write for State Plane, national grids and
/// Albers/Lambert projections.
/// </summary>
internal static partial class EsriProjectedCrsMatcher
{
    internal readonly record struct Signature(
        string CrsName,
        string Projection,
        string Datum,
        double Lon0,
        double? Lat0,
        double? Lat1,
        double? Lat2,
        double? Scale,
        double FalseEasting,
        double FalseNorthing,
        double MetersPerUnit)
    {
        public double FalseEastingMeters => FalseEasting * MetersPerUnit;

        public double FalseNorthingMeters => FalseNorthing * MetersPerUnit;

        public bool UsesUtmShorthand =>
            Projection == "tmerc"
            && Scale is double scale && Math.Abs(scale - 0.9996) < 1e-6
            && Math.Abs(FalseEasting - 500_000d) < 1d
            && Math.Abs(Lat0 ?? 0d) < 1e-6;

        public int UtmZone => (int)Math.Round((Lon0 + 183d) / 6d, MidpointRounding.AwayFromZero);

        public bool SouthernUtm => FalseNorthing > 1_000_000d;

        public string LongitudePattern => NumericTokenPattern("lon_0", Lon0);

        public string ZonePattern => $@"\+zone={UtmZone.ToString(CultureInfo.InvariantCulture)}(\s|$)";
    }

    internal readonly record struct CatalogRow(int Srid, string WellKnownText, string Proj4);

    public static string? ReadCrsName(string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        var match = ProjectedNamePattern().Match(wkt);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static bool TryParse(string wkt, out Signature signature)
    {
        signature = default;
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return false;
        }

        var projection = ProjectionToken(wkt);
        var name = ReadCrsName(wkt);
        if (projection is null || name is null || !TryParameter(wkt, "Central_Meridian", out var lon0))
        {
            return false;
        }

        var hasScale = TryParameter(wkt, "Scale_Factor", out var scale);
        TryParameter(wkt, "False_Easting", out var falseEasting);
        TryParameter(wkt, "False_Northing", out var falseNorthing);
        signature = new Signature(
            name,
            projection,
            DatumFamily(name, wkt),
            lon0,
            ParameterOrNull(wkt, "Latitude_Of_Origin"),
            ParameterOrNull(wkt, "Standard_Parallel_1"),
            ParameterOrNull(wkt, "Standard_Parallel_2"),
            hasScale ? scale : null,
            falseEasting,
            falseNorthing,
            MetersPerUnit(wkt));
        return true;
    }

    public static int? Select(Signature signature, IReadOnlyList<CatalogRow> rows)
    {
        var matched = rows.Where(row => ParametersMatch(signature, row.Proj4)).ToList();
        var sameDatum = matched.Where(row => DatumMatches(signature.Datum, row.WellKnownText)).ToList();
        var candidates = sameDatum.Count > 0 ? sameDatum : matched;
        // A 2D .prj must not land on a compound CRS that reuses the same horizontal parameters
        // (EPSG:7405 is British National Grid + ODN height, and its code is below 27700).
        var horizontal = candidates.Where(row => !IsVerticalCompound(row)).ToList();
        if (horizontal.Count == 1)
        {
            return horizontal[0].Srid;
        }

        if (horizontal.Count == 0)
        {
            return null;
        }

        return UniqueNameMatch(signature.CrsName, horizontal);
    }

    private static bool IsVerticalCompound(CatalogRow row)
        => row.WellKnownText.Contains("COMPD_CS", StringComparison.OrdinalIgnoreCase)
            || row.WellKnownText.Contains("COMPOUNDCRS", StringComparison.OrdinalIgnoreCase)
            || row.Proj4.Contains("+vunits=", StringComparison.Ordinal);

    private static int? UniqueNameMatch(string crsName, List<CatalogRow> candidates)
    {
        var tokens = NameTokens(crsName);
        var bestScore = 1;
        var bestSrid = 0;
        foreach (var candidate in candidates)
        {
            var score = tokens.Count(token => candidate.WellKnownText.Contains(token, StringComparison.OrdinalIgnoreCase)) * 10
                - RealizationPenalty(crsName, candidate.WellKnownText);
            if (score > bestScore || (score == bestScore && bestSrid != 0 && candidate.Srid < bestSrid))
            {
                bestScore = score;
                bestSrid = candidate.Srid;
            }
        }

        // Equal names (a catalogue duplicate of the same grid) keep the original, lower EPSG code.
        return bestSrid == 0 ? null : bestSrid;
    }

    // A later realization (v2, HARN, NSRS2007) shares projection parameters with the original.
    // The .prj name has to say so; otherwise the plain row wins.
    private static int RealizationPenalty(string crsName, string wellKnownText)
    {
        var penalty = 0;
        foreach (var marker in new[] { "v2b", "v2", "2011", "HARN", "NSRS", "CSRS" })
        {
            if (wellKnownText.Contains(marker, StringComparison.OrdinalIgnoreCase)
                && !crsName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                penalty++;
            }
        }

        return penalty;
    }

    private static bool ParametersMatch(Signature signature, string proj4)
    {
        if (string.IsNullOrWhiteSpace(proj4))
        {
            return false;
        }

        if (signature.UsesUtmShorthand && UtmShorthandMatches(signature, proj4))
        {
            return true;
        }

        if (!proj4.Contains($"+proj={signature.Projection}", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Near(Read(proj4, "lon_0"), signature.Lon0, 1e-4))
        {
            return false;
        }

        if (!OptionalNear(proj4, "lat_0", signature.Lat0, 1e-4)
            || !ParallelsMatch(signature, proj4)
            || !ScaleMatches(proj4, signature.Scale))
        {
            return false;
        }

        return OffsetsMatch(signature, proj4);
    }

    private static bool UtmShorthandMatches(Signature signature, string proj4)
    {
        if (!proj4.Contains("+proj=utm", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Near(Read(proj4, "zone"), signature.UtmZone, 0.1))
        {
            return false;
        }

        return proj4.Contains("+south", StringComparison.Ordinal) == signature.SouthernUtm;
    }

    private static bool OffsetsMatch(Signature signature, string proj4)
    {
        var x0 = Read(proj4, "x_0") ?? 0d;
        var y0 = Read(proj4, "y_0") ?? 0d;
        var feetDefinition = proj4.Contains("+to_meter=", StringComparison.Ordinal)
            || proj4.Contains("+units=us-ft", StringComparison.Ordinal)
            || proj4.Contains("+units=ft", StringComparison.Ordinal);
        // A foot CRS and a metre CRS for the same zone share the metre false easting.
        // Only a foot proj4 (to_meter or +units=us-ft/ft) may satisfy a foot .prj.
        if (signature.MetersPerUnit < 0.5d != feetDefinition)
        {
            return false;
        }

        var asMetres = Near(x0, signature.FalseEastingMeters, 0.05) && Near(y0, signature.FalseNorthingMeters, 0.05);
        if (!feetDefinition)
        {
            return asMetres;
        }

        // PostGIS stores +x_0 in metres even next to +units=us-ft. PROJ also emits the value in feet.
        var asStatedUnit = Near(x0, signature.FalseEasting, 0.05) && Near(y0, signature.FalseNorthing, 0.05);
        return asMetres || asStatedUnit;
    }

    private static bool OptionalNear(string proj4, string key, double? expected, double tolerance)
        => expected is not double value || Near(Read(proj4, key), value, tolerance);

    // Lambert definitions store the two standard parallels in either order.
    private static bool ParallelsMatch(Signature signature, string proj4)
    {
        if (signature.Lat1 is not double first)
        {
            return signature.Lat2 is null && Read(proj4, "lat_1") is null && Read(proj4, "lat_2") is null;
        }

        var second = signature.Lat2 ?? first;
        var actual1 = Read(proj4, "lat_1");
        var actual2 = Read(proj4, "lat_2");
        return (Near(actual1, first, 1e-4) && Near(actual2, second, 1e-4))
            || (Near(actual1, second, 1e-4) && Near(actual2, first, 1e-4));
    }

    // PROJ writes the scale as +k or +k_0 depending on the export.
    private static bool ScaleMatches(string proj4, double? scale)
        => scale is not double expected || Near(Read(proj4, "k") ?? Read(proj4, "k_0"), expected, 1e-6);

    private static bool Near(double? actual, double expected, double tolerance)
        => actual is double value && Math.Abs(value - expected) <= tolerance;

    private static double? Read(string proj4, string key)
    {
        var match = Regex.Match(proj4, $@"\+{key}=([+-]?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? ProjectionToken(string wkt)
    {
        var match = Regex.Match(wkt, @"PROJECTION\s*\[\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups[1].Value;
        if (name.StartsWith("Lambert_Conformal_Conic", StringComparison.OrdinalIgnoreCase))
        {
            return "lcc";
        }

        if (name.StartsWith("Albers", StringComparison.OrdinalIgnoreCase))
        {
            return "aea";
        }

        if (name.Equals("Transverse_Mercator", StringComparison.OrdinalIgnoreCase))
        {
            return "tmerc";
        }

        return null;
    }

    private static bool TryParameter(string wkt, string name, out double value)
    {
        var match = Regex.Match(
            wkt,
            $@"PARAMETER\s*\[\s*""{name}""\s*,\s*([+-]?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            value = 0d;
            return false;
        }

        return double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out value);
    }

    private static double? ParameterOrNull(string wkt, string name)
        => TryParameter(wkt, name, out var value) ? value : null;

    private static double MetersPerUnit(string wkt)
    {
        double meters = 1d;
        foreach (Match match in UnitPattern().Matches(wkt))
        {
            if (!double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var factor) || factor < 0.1d)
            {
                continue;
            }

            meters = factor;
        }

        return meters;
    }

    private static readonly (string Token, string Family)[] DatumTokens =
    [
        ("2011", "nad83-2011"),
        ("HARN", "nad83-harn"),
        ("HPGN", "nad83-harn"),
        ("CSRS", "nad83-csrs"),
        ("NAD_1983", "nad83"),
        ("NAD83", "nad83"),
        ("North_American_1983", "nad83"),
        ("GDA_1994", "gda94"),
        ("GDA94", "gda94"),
        ("GDA_2020", "gda2020"),
        ("GDA2020", "gda2020"),
        ("RGF", "rgf93"),
        ("OSGB", "osgb36"),
        ("Airy", "osgb36"),
        ("ETRS", "etrs89"),
        ("WGS", "wgs84"),
    ];

    private static string DatumFamily(string crsName, string wkt)
    {
        var blob = crsName + " " + wkt;
        foreach (var (token, family) in DatumTokens)
        {
            if (blob.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return "unknown";
    }

    private static bool DatumMatches(string datum, string wellKnownText)
    {
        return datum switch
        {
            "nad83-2011" => wellKnownText.Contains("2011", StringComparison.OrdinalIgnoreCase),
            "nad83-harn" => wellKnownText.Contains("HARN", StringComparison.OrdinalIgnoreCase),
            "nad83-csrs" => wellKnownText.Contains("CSRS", StringComparison.OrdinalIgnoreCase),
            "nad83" => wellKnownText.Contains("NAD83", StringComparison.OrdinalIgnoreCase)
                && !wellKnownText.Contains("2011", StringComparison.Ordinal)
                && !wellKnownText.Contains("HARN", StringComparison.OrdinalIgnoreCase)
                && !wellKnownText.Contains("CSRS", StringComparison.OrdinalIgnoreCase)
                && !wellKnownText.Contains("NSRS", StringComparison.OrdinalIgnoreCase),
            "gda94" => wellKnownText.Contains("GDA94", StringComparison.OrdinalIgnoreCase),
            "gda2020" => wellKnownText.Contains("GDA2020", StringComparison.OrdinalIgnoreCase),
            "rgf93" => wellKnownText.Contains("RGF93", StringComparison.OrdinalIgnoreCase),
            "osgb36" => wellKnownText.Contains("OSGB", StringComparison.OrdinalIgnoreCase),
            "etrs89" => wellKnownText.Contains("ETRS89", StringComparison.OrdinalIgnoreCase),
            "wgs84" => wellKnownText.Contains("WGS", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static List<string> NameTokens(string crsName)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NAD", "1983", "2011", "StatePlane", "FIPS", "Feet", "Foot", "Ft", "US", "GCS", "Zone", "HARN",
        };
        return crsName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4 && !skip.Contains(token) && !int.TryParse(token, out _))
            .ToList();
    }

    private static string NumericTokenPattern(string key, double value)
    {
        var text = Math.Round(value, 6, MidpointRounding.AwayFromZero)
            .ToString("0.######", CultureInfo.InvariantCulture);
        return $@"\+{key}={Regex.Escape(text)}(\.0+)?(\s|$)";
    }

    [GeneratedRegex(@"PROJC(?:S|RS)\s*\[\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectedNamePattern();

    [GeneratedRegex(@"UNIT\s*\[\s*""([^""]+)""\s*,\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern();
}
