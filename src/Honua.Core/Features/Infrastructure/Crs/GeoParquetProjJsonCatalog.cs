// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Features.Infrastructure.Crs;

// PROJJSON is embedded because the server has no PROJ library and PostGIS cannot emit it; an unknown SRID is an error rather than a hand-written CRS.
public static class GeoParquetProjJsonCatalog
{
    private const string ResourceName =
        "Honua.Core.Features.Infrastructure.Crs.Resources.geoparquet-crs-projjson.json";

    // Web Mercator alias SRIDs (Esri/legacy) collapse to the canonical EPSG:3857 definition.
    private static readonly FrozenDictionary<int, int> _aliases = new Dictionary<int, int>
    {
        [900913] = 3857,
        [102100] = 3857,
        [102113] = 3857,
        [3785] = 3857,
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<int, string> _catalog = Load();

    public static bool TryGetProjJson(int srid, [NotNullWhen(true)] out string? projJson)
    {
        var normalized = _aliases.TryGetValue(srid, out var alias) ? alias : srid;
        return _catalog.TryGetValue(normalized, out projJson);
    }

    public static bool IsSupported(int srid) => TryGetProjJson(srid, out _);

    public static IReadOnlyCollection<int> SupportedSrids => _catalog.Keys;

    // This definition resolves an explicit OGC:CRS84 source and does not change the EPSG:4326 export lookup.
    public static string Crs84ProjJson { get; } = LoadCrs84();

    private static string LoadCrs84()
    {
        using var stream = typeof(GeoParquetProjJsonCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded GeoParquet CRS catalog was not found.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("crs84").GetRawText();
    }

    private static FrozenDictionary<int, string> Load()
    {
        var assembly = typeof(GeoParquetProjJsonCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded GeoParquet PROJJSON catalog '{ResourceName}' was not found.");

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("crs", out var crs) ||
            crs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "GeoParquet PROJJSON catalog is missing the required 'crs' object.");
        }

        var map = new Dictionary<int, string>();
        foreach (var entry in crs.EnumerateObject()
                     .Select(property => (
                         Property: property,
                         Srid: int.TryParse(
                             property.Name,
                             NumberStyles.Integer,
                             CultureInfo.InvariantCulture,
                             out var srid)
                                 ? (int?)srid
                                 : null))
                     .Where(entry => entry.Srid.HasValue))
        {
            // GetRawText() returns the stored compact PROJJSON verbatim so it can be
            // spliced directly into the GeoParquet 'geo' metadata without re-serialization.
            map[entry.Srid.GetValueOrDefault()] = entry.Property.Value.GetRawText();
        }

        return map.ToFrozenDictionary();
    }
}
