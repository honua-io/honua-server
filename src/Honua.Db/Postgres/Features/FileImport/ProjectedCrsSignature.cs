// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Db.Postgres.Features.FileImport;

// The parts of a WKT1 projected CRS that decide where coordinates land: projection family, canonical
// projection parameters, linear unit, datum and prime meridian. ESRI .prj files (no AUTHORITY node, ESRI
// names) and PostGIS spatial_ref_sys srtext (OGC names) describe the same CRS with different text, so
// detection compares these values instead of the text.
internal sealed record ProjectedCrsSignature(ProjectionDefinition Projection, double LinearUnit, CrsDatum Datum)
{
    private const double AngleTolerance = 1e-7;
    private const double MetreTolerance = 1e-3;
    private const double ScaleTolerance = 1e-9;
    private const double UnitRelativeTolerance = 1e-9;

    private static readonly FrozenDictionary<string, string> _projectionFamilies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Transverse_Mercator"] = "tmerc",
        ["Gauss_Kruger"] = "tmerc",
        ["Lambert_Conformal_Conic"] = "lcc",
        ["Lambert_Conformal_Conic_1SP"] = "lcc",
        ["Lambert_Conformal_Conic_2SP"] = "lcc",
        ["Albers"] = "aea",
        ["Albers_Conic_Equal_Area"] = "aea",
        ["Lambert_Azimuthal_Equal_Area"] = "laea",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> _parameterNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["false_easting"] = "fe",
        ["false_northing"] = "fn",
        ["central_meridian"] = "lon0",
        ["longitude_of_center"] = "lon0",
        ["longitude_of_origin"] = "lon0",
        ["latitude_of_origin"] = "lat0",
        ["latitude_of_center"] = "lat0",
        ["scale_factor"] = "k",
        ["standard_parallel_1"] = "sp1",
        ["standard_parallel_2"] = "sp2",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // SQL LIKE prefixes that select spatial_ref_sys rows of one projection family.
    public static IReadOnlyList<string> ProjectionPatterns(string family) =>
        _projectionFamilies.Where(pair => pair.Value == family)
            .Select(pair => $"%PROJECTION[\"{pair.Key}\"]%")
            .ToArray();

    public static ProjectedCrsSignature? TryCreate(string wkt)
    {
        var root = WktTree.TryParse(wkt);
        if (root is null || !root.Keyword.Equals("PROJCS", StringComparison.OrdinalIgnoreCase))
            return null;
        var projectionName = root.Child("PROJECTION")?.Text(0);
        var unit = root.Child("UNIT")?.Number(1);
        var datum = CrsDatum.TryCreate(root.Child("GEOGCS"));
        if (projectionName is null || unit is not > 0 || datum is null
            || !_projectionFamilies.TryGetValue(projectionName, out var family))
            return null;
        var parameters = ReadParameters(root);
        return parameters is null
            ? null
            : new ProjectedCrsSignature(ProjectionDefinition.Create(family, parameters), unit.Value, datum);
    }

    public bool Matches(ProjectedCrsSignature candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var (mine, theirs) = (Projection, candidate.Projection);
        return mine.Family == theirs.Family
            && Math.Abs(LinearUnit - candidate.LinearUnit) <= UnitRelativeTolerance * LinearUnit
            && Datum.Matches(candidate.Datum)
            && Close(mine.Value("lon0"), theirs.Value("lon0"), AngleTolerance)
            && Close(mine.Value("lat0"), theirs.Value("lat0"), AngleTolerance)
            && Close(mine.Value("k", 1), theirs.Value("k", 1), ScaleTolerance)
            && Close(mine.Value("fe") * LinearUnit, theirs.Value("fe") * candidate.LinearUnit, MetreTolerance)
            && Close(mine.Value("fn") * LinearUnit, theirs.Value("fn") * candidate.LinearUnit, MetreTolerance)
            && mine.StandardParallels.Length == theirs.StandardParallels.Length
            && mine.StandardParallels.Zip(theirs.StandardParallels).All(pair => Close(pair.First, pair.Second, AngleTolerance));
    }

    private static bool Close(double left, double right, double tolerance) => Math.Abs(left - right) <= tolerance;

    private static Dictionary<string, double>? ReadParameters(WktNode root)
    {
        var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in root.Children("PARAMETER"))
        {
            var name = parameter.Text(0);
            var value = parameter.Number(1);
            // An unrecognised parameter could change the projection; refuse to guess.
            if (name is null || value is null || !_parameterNames.TryGetValue(name, out var canonical))
                return null;
            parameters[canonical] = value.Value;
        }

        return parameters;
    }
}

// A projection family with canonical parameter values. Standard parallels are an unordered set; a
// one-standard-parallel Lambert (ESRI writes the parallel, OGC writes only the latitude of origin) normalises to
// the single parallel at the latitude of origin.
internal sealed record ProjectionDefinition(string Family, FrozenDictionary<string, double> Parameters, double[] StandardParallels)
{
    private static readonly string[] _standardParallelNames = ["sp1", "sp2"];

    public double Value(string name, double fallback = 0) => Parameters.GetValueOrDefault(name, fallback);

    public static ProjectionDefinition Create(string family, Dictionary<string, double> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var parallels = _standardParallelNames
            .Where(parameters.ContainsKey)
            .Select(name => parameters[name])
            .ToList();
        if (family == "lcc" && parallels.Count == 0 && parameters.TryGetValue("lat0", out var origin))
            parallels.Add(origin);
        var distinct = parallels.Order().DistinctBy(value => Math.Round(value * 1e7)).ToArray();
        var rest = parameters.Where(pair => pair.Key is not ("sp1" or "sp2"))
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new ProjectionDefinition(family, rest, distinct);
    }
}

// The horizontal datum of a WKT1 GEOGCS: its EPSG datum code when the WKT carries one or the ESRI datum name
// is known, plus the ellipsoid and prime meridian.
internal sealed record CrsDatum(string? EpsgCode, (double SemiMajorAxis, double InverseFlattening) Ellipsoid, double PrimeMeridian)
{
    // ESRI datum names for the national and regional datums most .prj files use, mapped to EPSG datum codes.
    private static readonly FrozenDictionary<string, string> _esriDatumCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["D_WGS_1984"] = "6326",
        ["D_North_American_1983"] = "6269",
        ["D_North_American_1983_HARN"] = "6152",
        ["D_NAD_1983_NSRS2007"] = "6759",
        ["D_NAD_1983_2011"] = "1116",
        ["D_North_American_1983_CSRS"] = "6140",
        ["D_North_American_1927"] = "6267",
        ["D_OSGB_1936"] = "6277",
        ["D_ETRS_1989"] = "6258",
        ["D_RGF_1993"] = "6171",
        ["D_GDA_1994"] = "6283",
        ["D_GDA_2020"] = "1168",
        ["D_Deutsches_Hauptdreiecksnetz"] = "6314",
        ["D_Amersfoort"] = "6289",
        ["D_NZGD_2000"] = "6167",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static CrsDatum? TryCreate(WktNode? geographic)
    {
        var datum = geographic?.Child("DATUM");
        var spheroid = datum?.Child("SPHEROID");
        var semiMajor = spheroid?.Number(1);
        var inverseFlattening = spheroid?.Number(2);
        if (datum is null || semiMajor is not > 0 || inverseFlattening is null)
            return null;
        var authority = datum.Child("AUTHORITY");
        var code = authority?.Text(0)?.Equals("EPSG", StringComparison.OrdinalIgnoreCase) == true
            ? authority.Text(1)
            : _esriDatumCodes.GetValueOrDefault(datum.Text(0) ?? string.Empty);
        var primeMeridian = geographic!.Child("PRIMEM")?.Number(1) ?? 0;
        return new CrsDatum(code, (semiMajor.Value, inverseFlattening.Value), primeMeridian);
    }

    // With both datum codes known they must agree; otherwise only the ellipsoid can be compared, and the caller
    // must then insist on a single candidate.
    public bool Matches(CrsDatum other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Math.Abs(PrimeMeridian - other.PrimeMeridian) > 1e-9)
            return false;
        if (EpsgCode is not null && other.EpsgCode is not null)
            return EpsgCode == other.EpsgCode;
        return Math.Abs(Ellipsoid.SemiMajorAxis - other.Ellipsoid.SemiMajorAxis) <= 1e-3
            && Math.Abs(Ellipsoid.InverseFlattening - other.Ellipsoid.InverseFlattening) <= 1e-6;
    }
}
