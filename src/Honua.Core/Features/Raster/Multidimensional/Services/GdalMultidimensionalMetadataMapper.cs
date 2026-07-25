// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Services;

/// <summary>
/// Maps the JSON produced by GDAL <c>gdalmdiminfo</c> into the canonical
/// <see cref="MultidimensionalCoverageMetadata"/> domain model. This is the
/// pure, AOT-safe core of the Path B reader strategy recorded in ADR-0039:
/// the GDAL worker extracts structure from a NetCDF4/HDF5 source and this
/// mapper translates it; no native dependency reaches the server.
/// </summary>
/// <remarks>
/// <para>
/// <c>gdalmdiminfo</c> reports the cube's <em>structure</em> — variables,
/// data types, dimensions, chunk (<c>block_size</c>) layout, compression,
/// CF attributes (<c>units</c>, <c>standard_name</c>, <c>long_name</c>),
/// <c>nodata_value</c>, and the CF axis classification of each dimension
/// (<c>TEMPORAL</c> / <c>HORIZONTAL_X</c> / <c>HORIZONTAL_Y</c> /
/// <c>VERTICAL</c>). It does <em>not</em> emit coordinate variable
/// <em>values</em>, so the spatial <see cref="MultidimensionalCoverageMetadata.Extent"/>,
/// <see cref="MultidimensionalCoverageMetadata.Resolution"/>, and the
/// start/end bounds of <see cref="MultidimensionalCoverageMetadata.Temporal"/> /
/// <see cref="MultidimensionalCoverageMetadata.Vertical"/> cannot be derived
/// here. Those are populated by the convert-time enrichment pass (a classic
/// <c>gdalinfo -json</c> over the produced Zarr/2D view) — see ADR-0039
/// "Path Selection (#1756)".
/// </para>
/// </remarks>
public static class GdalMultidimensionalMetadataMapper
{
    private const string DegreesEast = "degrees_east";
    private const string DegreesNorth = "degrees_north";

    /// <summary>
    /// Maps a <c>gdalmdiminfo</c> JSON document to coverage metadata.
    /// </summary>
    /// <param name="gdalMdimInfoJson">Raw stdout of <c>gdalmdiminfo &lt;source&gt;</c>.</param>
    /// <param name="format">Container format of the registered source.</param>
    /// <param name="selectedVariables">
    /// Operator-declared variable names to expose. Empty means "every
    /// non-coordinate (data) variable discovered".
    /// </param>
    /// <returns>The structural coverage metadata.</returns>
    /// <exception cref="ArgumentException">The JSON is null/empty.</exception>
    /// <exception cref="MultidimensionalCoverageUnsupportedLayoutException">
    /// The document is not a parseable GDAL multidimensional group, or exposes
    /// no data variables matching the selection.
    /// </exception>
    public static MultidimensionalCoverageMetadata Map(
        string gdalMdimInfoJson,
        MultidimensionalCoverageFormat format,
        IReadOnlyList<string> selectedVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gdalMdimInfoJson);
        ArgumentNullException.ThrowIfNull(selectedVariables);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(gdalMdimInfoJson);
        }
        catch (JsonException ex)
        {
            throw new MultidimensionalCoverageUnsupportedLayoutException(
                "gdalmdiminfo output is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MultidimensionalCoverageUnsupportedLayoutException(
                    "gdalmdiminfo output is not a JSON object.");
            }

            var dimensions = ReadDimensions(root);
            var (variables, srid) = ReadVariables(root, dimensions, selectedVariables);

            if (variables.Count == 0)
            {
                throw new MultidimensionalCoverageUnsupportedLayoutException(
                    selectedVariables.Count > 0
                        ? "None of the declared variables match a data variable in the source."
                        : "The source exposes no multidimensional data variables.");
            }

            return new MultidimensionalCoverageMetadata
            {
                Format = format,
                Srid = srid,
                Extent = null,
                Resolution = (0d, 0d),
                Temporal = null,
                Vertical = null,
                Variables = variables,
            };
        }
    }

    private static Dictionary<string, DimensionInfo> ReadDimensions(JsonElement root)
    {
        var byFullName = new Dictionary<string, DimensionInfo>(StringComparer.Ordinal);
        if (!root.TryGetProperty("dimensions", out var dims) || dims.ValueKind != JsonValueKind.Array)
        {
            return byFullName;
        }

        foreach (var dim in dims.EnumerateArray())
        {
            if (dim.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(dim, "name") ?? string.Empty;
            var fullName = GetString(dim, "full_name") ?? name;
            var size = dim.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0L;
            var axisType = GetString(dim, "type");
            var unit = ReadIndexingVariableUnit(dim);

            byFullName[fullName] = new DimensionInfo(name, fullName, size, axisType, unit);
        }

        return byFullName;
    }

    private static (IReadOnlyList<MultidimensionalCoverageVariable> Variables, int Srid) ReadVariables(
        JsonElement root,
        Dictionary<string, DimensionInfo> dimensions,
        IReadOnlyList<string> selectedVariables)
    {
        var variables = new List<MultidimensionalCoverageVariable>();
        if (!root.TryGetProperty("arrays", out var arrays) || arrays.ValueKind != JsonValueKind.Object)
        {
            return (variables, 0);
        }

        // The indexing (coordinate) variables share their name with a dimension;
        // they describe axes, not data, so they are not exposed as coverage fields.
        var coordinateFullNames = new HashSet<string>(dimensions.Keys, StringComparer.Ordinal);

        var selected = selectedVariables.Count > 0
            ? new HashSet<string>(selectedVariables, StringComparer.Ordinal)
            : null;

        foreach (var array in arrays.EnumerateObject())
        {
            var element = array.Value;
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = array.Name;
            var fullName = GetString(element, "full_name") ?? name;

            if (selected is null)
            {
                // Auto-discovery: skip coordinate variables.
                if (coordinateFullNames.Contains(fullName))
                {
                    continue;
                }
            }
            else if (!selected.Contains(name))
            {
                continue;
            }

            variables.Add(ReadVariable(name, element, dimensions));
        }

        var srid = InferSrid(root, dimensions);
        return (variables, srid);
    }

    private static MultidimensionalCoverageVariable ReadVariable(
        string name,
        JsonElement element,
        Dictionary<string, DimensionInfo> dimensions)
    {
        var dataType = GetString(element, "datatype") ?? "Unknown";

        var variableDimensions = new List<MultidimensionalCoverageDimension>();
        if (element.TryGetProperty("dimensions", out var dimRefs) && dimRefs.ValueKind == JsonValueKind.Array)
        {
            var sizes = ReadInt64Array(element, "dimension_size");
            var index = 0;
            foreach (var dimRef in dimRefs.EnumerateArray())
            {
                var fullName = dimRef.ValueKind == JsonValueKind.String ? dimRef.GetString() ?? string.Empty : string.Empty;
                long size = index < sizes.Count ? sizes[index] : 0L;
                var found = dimensions.TryGetValue(fullName, out var info);
                var dimName = found ? info.Name : ShortName(fullName);
                if (size == 0 && found)
                {
                    size = info.Size;
                }

                variableDimensions.Add(new MultidimensionalCoverageDimension(dimName, size));
                index++;
            }
        }

        var chunkLayout = ReadChunkLayout(element);
        var units = GetString(element, "unit");
        string? longName = null;
        string? standardName = null;
        if (element.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            longName = GetString(attrs, "long_name");
            standardName = GetString(attrs, "standard_name");
        }

        double? noData = element.TryGetProperty("nodata_value", out var noDataEl) && noDataEl.ValueKind == JsonValueKind.Number
            ? noDataEl.GetDouble()
            : null;

        return new MultidimensionalCoverageVariable(
            name,
            dataType,
            variableDimensions,
            chunkLayout,
            units,
            longName,
            standardName,
            noData);
    }

    private static MultidimensionalChunkLayout? ReadChunkLayout(JsonElement element)
    {
        if (!element.TryGetProperty("block_size", out var blockEl) || blockEl.ValueKind != JsonValueKind.Array)
        {
            // No block_size => contiguous storage. Range-efficient reads rely on
            // chunking; the convert step re-chunks, so this is not rejected here.
            return null;
        }

        var chunkShape = new List<long>();
        chunkShape.AddRange(blockEl.EnumerateArray()
            .Select(entry => entry.TryGetInt64(out var value) ? (long?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value.GetValueOrDefault()));

        if (chunkShape.Count == 0)
        {
            return null;
        }

        var compression = "none";
        if (element.TryGetProperty("structural_info", out var structural) &&
            structural.ValueKind == JsonValueKind.Object)
        {
            var compress = GetString(structural, "COMPRESS");
            if (!string.IsNullOrWhiteSpace(compress))
            {
                compression = compress.ToLowerInvariant();
            }
        }

        // gdalmdiminfo does not surface the byte-shuffle filter flag in
        // structural_info; the convert-time pass records it on the Zarr output.
        return new MultidimensionalChunkLayout(chunkShape, compression, ShuffleFilter: false);
    }

    private static int InferSrid(JsonElement root, Dictionary<string, DimensionInfo> dimensions)
    {
        // Prefer an explicit GDAL spatial reference when present.
        if (root.TryGetProperty("arrays", out var arrays) && arrays.ValueKind == JsonValueKind.Object)
        {
            var resolvedEpsg = arrays.EnumerateObject()
                .Select(array =>
                    array.Value.ValueKind == JsonValueKind.Object &&
                    array.Value.TryGetProperty("srs", out var srs) &&
                    TryReadEpsgFromSrs(srs, out var epsg)
                        ? (int?)epsg
                        : null)
                .FirstOrDefault(epsg => epsg.HasValue);
            if (resolvedEpsg.HasValue)
            {
                return resolvedEpsg.Value;
            }
        }

        // CF/COARDS geographic inference: degrees_east X paired with degrees_north Y.
        var hasGeographicX = false;
        var hasGeographicY = false;
        foreach (var dim in dimensions.Values)
        {
            if (string.Equals(dim.Unit, DegreesEast, StringComparison.OrdinalIgnoreCase))
            {
                hasGeographicX = true;
            }
            else if (string.Equals(dim.Unit, DegreesNorth, StringComparison.OrdinalIgnoreCase))
            {
                hasGeographicY = true;
            }
        }

        return hasGeographicX && hasGeographicY ? 4326 : 0;
    }

    private static bool TryReadEpsgFromSrs(JsonElement srs, out int epsg)
    {
        epsg = 0;
        if (srs.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // GDAL emits the SRS as WKT; pull the trailing AUTHORITY EPSG code.
        var wkt = GetString(srs, "wkt");
        if (string.IsNullOrEmpty(wkt))
        {
            return false;
        }

        const string marker = "AUTHORITY[\"EPSG\",\"";
        var last = wkt.LastIndexOf(marker, StringComparison.Ordinal);
        if (last < 0)
        {
            return false;
        }

        var start = last + marker.Length;
        var end = wkt.IndexOf('"', start);
        if (end < 0)
        {
            return false;
        }

        return int.TryParse(wkt.AsSpan(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out epsg) && epsg > 0;
    }

    private static string? ReadIndexingVariableUnit(JsonElement dimension)
    {
        if (!dimension.TryGetProperty("indexing_variable", out var indexing) ||
            indexing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // indexing_variable is keyed by the coordinate variable's short name.
        foreach (var variable in indexing.EnumerateObject().Where(variable => variable.Value.ValueKind == JsonValueKind.Object))
        {
            return GetString(variable.Value, "unit");
        }

        return null;
    }

    private static List<long> ReadInt64Array(JsonElement element, string property)
    {
        var values = new List<long>();
        if (element.TryGetProperty(property, out var arrayEl) && arrayEl.ValueKind == JsonValueKind.Array)
        {
            values.AddRange(arrayEl.EnumerateArray()
                .Select(entry => entry.TryGetInt64(out var value) ? (long?)value : null)
                .Where(value => value.HasValue)
                .Select(value => value.GetValueOrDefault()));
        }

        return values;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ShortName(string fullName)
    {
        var slash = fullName.LastIndexOf('/');
        return slash >= 0 && slash < fullName.Length - 1 ? fullName[(slash + 1)..] : fullName;
    }

    private readonly record struct DimensionInfo(string Name, string FullName, long Size, string? AxisType, string? Unit);
}
