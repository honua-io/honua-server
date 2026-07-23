// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// Authoritative, embedded catalog of PROJJSON coordinate reference system definitions
/// used to populate the GeoParquet <c>geo</c> column <c>crs</c> metadata for non-default
/// output CRS.
/// </summary>
/// <remarks>
/// <para>
/// GeoParquet 1.1 stores each geometry column's CRS as a PROJJSON object. The .NET server
/// runtime has no in-process PROJ library, and PostGIS exposes no SRID → PROJJSON function,
/// so the PROJJSON cannot be synthesized at request time. Instead the definitions are
/// pre-generated from PROJ (via pyproj) for a curated set of common output CRS, committed
/// as an embedded resource, and looked up here. The generator lives at
/// <c>scripts/geoparquet/generate-projjson-catalog.py</c>; each entry is validated to
/// round-trip back to its own EPSG code and is byte-for-byte what geopandas / GDAL write
/// for the same CRS, so emitted files round-trip through GeoParquet readers.
/// </para>
/// <para>
/// The default GeoParquet CRS is OGC:CRS84 (longitude, latitude), which is emitted by
/// omitting the <c>crs</c> field; EPSG:4326 output therefore does not require a catalog
/// entry. SRIDs absent from the catalog are treated as unresolvable so callers can surface
/// a precise error rather than emitting hand-rolled or hardcoded CRS metadata.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Attempts to resolve the PROJJSON definition for an output SRID.
    /// </summary>
    /// <param name="srid">EPSG SRID (Web Mercator aliases are normalized to 3857).</param>
    /// <param name="projJson">
    /// On success, the compact PROJJSON object as a JSON string suitable for embedding
    /// directly as the GeoParquet column <c>crs</c> value.
    /// </param>
    /// <returns><see langword="true"/> when a definition exists; otherwise <see langword="false"/>.</returns>
    public static bool TryGetProjJson(int srid, [NotNullWhen(true)] out string? projJson)
    {
        var normalized = _aliases.TryGetValue(srid, out var alias) ? alias : srid;
        return _catalog.TryGetValue(normalized, out projJson);
    }

    /// <summary>
    /// Indicates whether a PROJJSON definition is resolvable for the given SRID.
    /// </summary>
    /// <param name="srid">EPSG SRID (Web Mercator aliases are normalized to 3857).</param>
    /// <returns><see langword="true"/> when a definition exists; otherwise <see langword="false"/>.</returns>
    public static bool IsSupported(int srid) => TryGetProjJson(srid, out _);

    /// <summary>
    /// The set of EPSG SRIDs with a resolvable PROJJSON definition (excludes alias SRIDs).
    /// </summary>
    public static IReadOnlyCollection<int> SupportedSrids => _catalog.Keys;

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
        // codeql[cs/linq/missed-where] -- predicate binds state or awaits; retain imperative control flow.
        foreach (var property in crs.EnumerateObject())
        {
            if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid))
            {
                // GetRawText() returns the stored compact PROJJSON verbatim so it can be
                // spliced directly into the GeoParquet 'geo' metadata without re-serialization.
                map[srid] = property.Value.GetRawText();
            }
        }

        return map.ToFrozenDictionary();
    }
}
