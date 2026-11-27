// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalVectorDriverAllowlist
{
    internal readonly record struct VectorFormatSpec(string Extension, IReadOnlyList<string> Drivers);

    private static readonly FrozenDictionary<string, VectorFormatSpec> Formats =
        new Dictionary<string, VectorFormatSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["GeoJSON"] = new(".geojson", ["GeoJSON"]),
            ["GML"] = new(".gml", ["GML"]),
            ["KML"] = new(".kml", ["LIBKML", "KML"]),
            ["GPKG"] = new(".gpkg", ["GPKG"]),
            ["FlatGeobuf"] = new(".fgb", ["FlatGeobuf"]),
            ["MapInfo File"] = new(".tab", ["MapInfo File"]),
            ["CSV"] = new(".csv", ["CSV"]),
            ["ESRI Shapefile"] = new(".shp", ["ESRI Shapefile"]),
            ["OpenFileGDB"] = new(".gdb", ["OpenFileGDB"]),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, IReadOnlyList<string>> DriversByExtension =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [".geojson"] = ["GeoJSON"],
            [".json"] = ["GeoJSON"],
            [".gml"] = ["GML"],
            [".kml"] = ["LIBKML", "KML"],
            [".gpkg"] = ["GPKG"],
            [".fgb"] = ["FlatGeobuf"],
            [".tab"] = ["MapInfo File"],
            [".csv"] = ["CSV"],
            [".shp"] = ["ESRI Shapefile"],
            [".gdb"] = ["OpenFileGDB"],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string SupportedFormatList { get; } =
        string.Join(", ", Formats.Keys.OrderBy(key => key, StringComparer.Ordinal));

    public static VectorFormatSpec? ResolveFormat(string? sourceFormat)
    {
        if (string.IsNullOrWhiteSpace(sourceFormat))
        {
            return null;
        }

        return Formats.TryGetValue(sourceFormat.Trim(), out var spec) ? spec : null;
    }

    public static IReadOnlyList<string> ResolveDriversForPath(string datasetPath)
    {
        if (string.IsNullOrWhiteSpace(datasetPath))
        {
            return [];
        }

        var trimmed = datasetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var extension = Path.GetExtension(trimmed);
        return DriversByExtension.TryGetValue(extension, out var drivers) ? drivers : [];
    }

    public static IReadOnlyList<string> PrefixArguments(
        IReadOnlyList<string> drivers,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(arguments);

        if (drivers.Count == 0)
        {
            throw new InvalidOperationException(
                "GDAL vector input driver allowlist is empty. Refusing to run 'ogr2ogr' without -if drivers.");
        }

        var prefixed = new List<string>(arguments.Count + (drivers.Count * 2));
        foreach (var driver in drivers)
        {
            prefixed.Add("-if");
            prefixed.Add(driver);
        }

        prefixed.AddRange(arguments);
        return prefixed;
    }
}
