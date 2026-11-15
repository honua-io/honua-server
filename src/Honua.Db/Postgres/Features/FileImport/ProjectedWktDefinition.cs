// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed class ProjectedWktDefinition
{
    private string Datum { get; init; } = string.Empty;
    private string Projection { get; init; } = string.Empty;
    private string Axes { get; init; } = string.Empty;
    private Dictionary<string, double> Numbers { get; init; } = new();
    private double[]? Transform { get; init; }

    internal static ProjectedWktDefinition? Parse(string text)
    {
        try
        {
            var root = ProjectedWktNode.Parse(text);
            if (root.Kind != "PROJCS") return null;
            root.Validate(1, "GEOGCS", "PROJECTION", "PARAMETER", "UNIT", "AXIS", "AUTHORITY");
            var geographic = root.One("GEOGCS");
            geographic.Validate(1, "DATUM", "PRIMEM", "UNIT", "AXIS", "AUTHORITY");
            var datum = geographic.One("DATUM");
            datum.Validate(1, "SPHEROID", "TOWGS84", "AUTHORITY");
            var spheroid = datum.One("SPHEROID");
            spheroid.Validate(3, "AUTHORITY");
            var projection = root.One("PROJECTION");
            projection.Validate(1, "AUTHORITY");
            var numbers = ReadNumbers(root, geographic, spheroid);
            var method = CanonicalProjection(projection.Text(0), numbers);
            if (method == "albersconicequalarea") NormalizeAlbersParameters(numbers);
            var transform = datum.Children("TOWGS84").SingleOrDefault();
            return new ProjectedWktDefinition
            {
                Datum = CanonicalDatum(datum.Text(0)), Projection = method,
                Axes = ReadAxes(root), Numbers = numbers, Transform = ReadTransform(transform)
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static Dictionary<string, double> ReadNumbers(
        ProjectedWktNode root, ProjectedWktNode geographic, ProjectedWktNode spheroid)
    {
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in root.Children("PARAMETER"))
        {
            parameter.Validate(2, "AUTHORITY");
            numbers.Add(Normalize(parameter.Text(0)), parameter.Number(1));
        }
        numbers.Add("ellipsoidmajor", spheroid.Number(1));
        numbers.Add("ellipsoidinverseflattening", spheroid.Number(2));
        numbers.Add("linearunit", ReadUnit(root.One("UNIT")));
        numbers.Add("angularunit", ReadUnit(geographic.One("UNIT")));
        var prime = geographic.One("PRIMEM");
        prime.Validate(2, "AUTHORITY");
        numbers.Add("primemeridian", prime.Number(1));
        return numbers;
    }

    private static double ReadUnit(ProjectedWktNode unit)
    {
        unit.Validate(2, "AUTHORITY");
        var factor = unit.Number(1);
        if (factor <= 0) throw new FormatException();
        return factor;
    }

    private static string ReadAxes(ProjectedWktNode root)
    {
        var axes = root.Children("AXIS").ToArray();
        if (axes.Length == 0) return "EAST,NORTH";
        if (axes.Length != 2) throw new FormatException();
        foreach (var axis in axes) axis.Validate(2);
        return string.Join(',', axes.Select(axis => axis.Text(1).ToUpperInvariant()));
    }

    private static double[]? ReadTransform(ProjectedWktNode? transform)
    {
        if (transform == null) return null;
        if (transform.Values.Count is not (3 or 7)) throw new FormatException();
        transform.Validate(transform.Values.Count);
        return Enumerable.Range(0, transform.Values.Count).Select(transform.Number).ToArray();
    }

    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string CanonicalDatum(string name)
    {
        var value = Normalize(name.StartsWith("D_", StringComparison.OrdinalIgnoreCase) ? name[2..] : name);
        return value switch
        {
            "northamerican1983" => "northamericandatum1983",
            "nad19832011" => "nad83nationalspatialreferencesystem2011",
            "rgf1993" => "reseaugeodesiquefrancais1993",
            "gda1994" => "geocentricdatumofaustralia1994",
            _ => value
        };
    }

    private static void NormalizeAlbersParameters(Dictionary<string, double> numbers)
    {
        foreach (var (alias, canonical) in new[]
        {
            ("latitudeofcenter", "latitudeoforigin"), ("longitudeofcenter", "centralmeridian")
        })
        {
            if (numbers.Remove(alias, out var value)) numbers.Add(canonical, value);
        }
    }

    private static string CanonicalProjection(string name, Dictionary<string, double> numbers) => Normalize(name) switch
    {
        "lambertconformalconic" when numbers.ContainsKey("standardparallel1") && numbers.ContainsKey("standardparallel2")
            => "lambertconformalconic2sp",
        "albers" => "albersconicequalarea",
        var value => value
    };

    internal bool Matches(ProjectedWktDefinition candidate)
    {
        if (Datum != candidate.Datum || Projection != candidate.Projection || Axes != candidate.Axes
            || Numbers.Count != candidate.Numbers.Count) return false;
        // ESRI omits legacy TOWGS84 hints; datum identity still has to match. An explicit
        // input transform must match too, so a custom datum shift cannot be silently discarded.
        if (Transform != null && (candidate.Transform == null || Transform.Length != candidate.Transform.Length
            || !Transform.Zip(candidate.Transform).All(pair => NearlyEqual(pair.First, pair.Second)))) return false;
        return Numbers.All(pair => candidate.Numbers.TryGetValue(pair.Key, out var number) && NearlyEqual(pair.Value, number));
    }

    private static bool NearlyEqual(double first, double second) =>
        Math.Abs(first - second) <= Math.Max(1e-12, Math.Max(Math.Abs(first), Math.Abs(second)) * 1e-12);
}
