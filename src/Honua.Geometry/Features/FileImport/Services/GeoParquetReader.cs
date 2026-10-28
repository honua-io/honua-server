// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using Honua.Core.Features.FeatureStore.Services;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Streaming GeoParquet format reader. Reads GeoParquet files using Parquet.Net,
/// extracting WKB geometry and attribute columns into NTS features.
/// </summary>
internal static class GeoParquetReader
{
    private const int YieldInterval = 256;

    /// <summary>
    /// Error message used when a GeoParquet file uses a non-WKB geometry encoding.
    /// Shared between the import and preview rejection paths.
    /// </summary>
    internal const string UnsupportedEncodingMessage =
        "GeoParquet native geometry encodings are valid GeoParquet 1.1, but this importer supports only WKB geometry encoding.";

    internal const string UnsupportedEdgesMessage =
        "GeoParquet spherical edges are not supported; this importer preserves planar edges only.";

    internal const string UnsupportedMultipleGeometryMessage =
        "GeoParquet multiple geometry columns cannot be preserved by this importer; select a single geometry column before importing.";

    /// <summary>
    /// Streams features from a GeoParquet file. The stream must be seekable.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        ImportLimits limits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("GeoParquet reader requires a seekable stream.");
        }

        stream.Position = 0;
        using var reader = await CreateReaderAsync(stream, cancellationToken);
        var geoMeta = ParseGeoMetadata(reader.CustomMetadata);
        ThrowIfUnsupported(geoMeta);
        var primaryColumn = geoMeta.PrimaryColumn;
        var wkbReader = new WKBReader();
        var schema = reader.Schema;

        // Find geometry column index; exclude all geometry columns from attributes
        var geometryFieldIndex = -1;
        var geometryColumnNames = geoMeta.GeometryColumnNames;
        var attributeFields = new List<(int Index, DataField Field)>();

        for (var i = 0; i < schema.Fields.Count; i++)
        {
            if (schema.Fields[i] is DataField df)
            {
                if (string.Equals(df.Name, primaryColumn, StringComparison.Ordinal))
                {
                    geometryFieldIndex = i;
                }
                else if (!geometryColumnNames.Contains(df.Name))
                {
                    attributeFields.Add((i, df));
                }
            }
            // Nested attributes are reconstructed through Arrow below; scalar columns retain their declared types.
        }

        if (geometryFieldIndex < 0)
        {
            throw new InvalidDataException($"GeoParquet geometry column '{primaryColumn}' not found in schema.");
        }

        // Parquet is a columnar format — Parquet.Net reads all rows of a column within a
        // row group in a single ReadColumnAsync call. The row group is still the natural
        // streaming unit: its column buffers are released before the next group is read.
        var recordCount = 0;
        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rowGroupMetadata = reader.Metadata?.RowGroups[rg];
            if (limits.MaxMemoryBytes > 0 && rowGroupMetadata?.TotalByteSize > limits.MaxMemoryBytes)
            {
                throw new InvalidDataException(
                    $"GeoParquet row group {rg} declares {rowGroupMetadata.TotalByteSize:N0} uncompressed bytes, "
                    + $"which exceeds ImportLimits.MaxMemoryBytes ({limits.MaxMemoryBytes:N0}). "
                    + "Split the file into smaller row groups or increase the configured memory limit.");
            }

            var nestedAttributes = await GeoParquetNestedAttributeReader.ReadRowGroupAsync(
                stream, schema.Fields.Where(field => field is not DataField && !geometryColumnNames.Contains(field.Name))
                    .Select(field => field.Name).ToArray(), rg, limits, cancellationToken);
            using var rowGroupReader = reader.OpenRowGroupReader(rg);

            // Read geometry column
            var geometryColumn = await rowGroupReader.ReadColumnAsync(
                (DataField)schema.Fields[geometryFieldIndex], cancellationToken);
            var geometryData = geometryColumn.Data as byte[]?[];
            if (geometryData is null)
            {
                throw new InvalidDataException(
                    $"Geometry column '{primaryColumn}' does not contain binary (WKB) data.");
            }

            // Read attribute columns
            var attributeData = new List<(string Name, Array Data)>();
            foreach (var (index, field) in attributeFields)
            {
                var column = await rowGroupReader.ReadColumnAsync(field, cancellationToken);
                attributeData.Add((field.Name, column.Data));
            }

            var rowCount = geometryData.Length;
            for (var row = 0; row < rowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attributes = new AttributesTable();
                foreach (var (name, data) in attributeData)
                {
                    var value = data.GetValue(row);
                    if (value is DateTimeOffset dto)
                    {
                        attributes.Add(name, dto);
                    }
                    else if (value is DateTime dt)
                    {
                        attributes.Add(name, new DateTimeOffset(dt, TimeSpan.Zero));
                    }
                    else
                    {
                        attributes.Add(name, value);
                    }
                }

                foreach (var (name, values) in nestedAttributes)
                {
                    if (values.Count != geometryData.Length)
                    {
                        throw new InvalidDataException("GeoParquet nested attribute row count does not match the geometry column.");
                    }
                    attributes.Add(name, values[row]);
                }

                NetTopologySuite.Geometries.Geometry? geometry = null;
                var wkbBytes = geometryData[row];
                if (wkbBytes != null)
                {
                    if (!WkbSridNormalizer.TryGetDeclaredOrdinates(wkbBytes, out _, out var hasM) || hasM)
                    {
                        throw new InvalidDataException("GeoParquet WKB must contain supported XY or XYZ geometry types; measured, nonlinear, or malformed WKB is not accepted.");
                    }
                    try
                    {
                        geometry = wkbReader.Read(wkbBytes);
                    }
                    catch (Exception ex) when (ex is ParseException or FormatException)
                    {
                        throw new InvalidDataException(
                            $"Row {row} in row group {rg} contains invalid WKB geometry data.", ex);
                    }
                }

                yield return new Feature(geometry, attributes);

                recordCount++;
                if (recordCount % YieldInterval == 0)
                {
                    await Task.Yield();
                }
            }
        }
    }

    /// <summary>
    /// Extracts all import-relevant metadata from a GeoParquet stream in a single pass:
    /// SRID, encoding support, and warnings. Resets stream position to 0 after reading.
    /// </summary>
    internal static async Task<GeoParquetFileMetadata> ExtractMetadataAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("GeoParquet metadata extraction requires a seekable stream.");
        }

        stream.Position = 0;
        using var reader = await CreateReaderAsync(stream, cancellationToken);
        var geoMeta = ParseGeoMetadata(reader.CustomMetadata);
        var isWkbEncoding = string.Equals(geoMeta.Encoding, "WKB", StringComparison.Ordinal);

        var warnings = new List<string>();

        // Check encoding
        if (!isWkbEncoding)
        {
            warnings.Add($"GeoParquet file uses '{geoMeta.Encoding}' geometry encoding. This importer supports only WKB geometry encoding.");
        }

        // GeoParquet leaves the CRS key optional and implies OGC:CRS84 when absent.
        // We map CRS84 to SRID 4326, which is an axis-order mismatch that silently
        // "just works" for most consumers but can surprise clients that assume
        // EPSG:4326 lat-lon ordering. Surface it so importers can audit their source.
        if (geoMeta.Crs84Implied)
        {
            warnings.Add(
                "GeoParquet file omits 'crs' metadata; defaulting to OGC:CRS84 and storing as SRID 4326. "
                + "OGC:CRS84 is lon-lat while EPSG:4326 is officially lat-lon — downstream clients that "
                + "assume lat-lon order on SRID 4326 may interpret coordinates incorrectly.");
        }

        // Check for multiple geometry columns
        if (geoMeta.GeometryColumnCount > 1)
        {
            warnings.Add(UnsupportedMultipleGeometryMessage);
        }

        if (!geoMeta.IsPlanar)
        {
            warnings.Add(UnsupportedEdgesMessage);
        }

        var schema = reader.Schema;
        // Verify the exact primary column exists, including valid native nested encodings.
        // Fail fast here instead of deferring to ReadStreamingAsync, which would read
        // attribute columns before discovering the mismatch.
        var hasPrimaryColumn = schema.Fields.Any(field =>
            string.Equals(field.Name, geoMeta.PrimaryColumn, StringComparison.Ordinal));

        if (!hasPrimaryColumn)
        {
            throw new InvalidDataException(
                $"GeoParquet geometry column '{geoMeta.PrimaryColumn}' not found in Parquet schema.");
        }

        var totalRowCount = reader.Metadata?.NumRows ?? 0;
        stream.Position = 0;
        return new GeoParquetFileMetadata(geoMeta.Srid, isWkbEncoding, warnings.ToArray(), totalRowCount, GetUnsupportedReason(geoMeta));
    }

    /// <summary>
    /// Import-relevant metadata extracted from a GeoParquet file in a single reader pass.
    /// </summary>
    internal sealed record GeoParquetFileMetadata(
        int? Srid,
        bool IsWkbEncoding,
        string[] Warnings,
        long TotalRowCount,
        string? UnsupportedReason);

    /// <summary>
    /// Opens a Parquet reader, normalizing library-level I/O and format errors
    /// (e.g. invalid magic bytes) to <see cref="InvalidDataException"/> so callers
    /// can treat them as client input errors rather than server faults.
    /// </summary>
    private static async Task<ParquetReader> CreateReaderAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            throw new InvalidDataException("File is not a valid Parquet file.", ex);
        }
    }

    private static GeoParquetMetadata ParseGeoMetadata(Dictionary<string, string>? customMetadata)
    {
        if (customMetadata == null || !customMetadata.TryGetValue("geo", out var geoJson))
        {
            throw new InvalidDataException("GeoParquet file is missing required 'geo' metadata key.");
        }

        if (Encoding.UTF8.GetByteCount(geoJson) > 1048576)
        {
            throw new InvalidDataException("GeoParquet 'geo' metadata exceeds the supported byte budget.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(geoJson, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GeoParquet file contains malformed 'geo' metadata JSON.", ex);
        }

        using (doc)
        {
            try
            {
                return ParseGeoDocument(doc);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException(
                    "GeoParquet 'geo' metadata has unexpected structure (wrong JSON value types).", ex);
            }
        }
    }

    private static GeoParquetMetadata ParseGeoDocument(JsonDocument doc)
    {
        var root = doc.RootElement;
        ValidateUniqueMetadataProperties(root, ["version", "primary_column", "columns"]);
        var version = RequiredString(root, "version");
        if (version is not ("1.0.0" or "1.1.0"))
        {
            throw new NotSupportedException("GeoParquet metadata version is not supported; expected 1.0.0 or 1.1.0.");
        }

        var primaryColumn = RequiredString(root, "primary_column");
        if (!root.TryGetProperty("columns"u8, out var columns) || columns.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GeoParquet metadata requires a 'columns' object.");
        }

        ValidateUniqueMetadataProperties(columns);

        string? encoding = null;
        int? srid = null;
        var geometryColumnNames = new HashSet<string>(StringComparer.Ordinal);
        var crs84Implied = false;
        var isPlanar = true;
        foreach (var column in columns.EnumerateObject())
        {
            if (!geometryColumnNames.Add(column.Name) || column.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("GeoParquet geometry columns must have unique names and object metadata.");
            }

            ValidateUniqueMetadataProperties(column.Value,
                ["encoding", "geometry_types", "crs", "edges", "orientation", "bbox", "epoch", "covering"]);
            var columnEncoding = RequiredString(column.Value, "encoding");
            if (columnEncoding is not ("WKB" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring" or "multipolygon") ||
                (version == "1.0.0" && columnEncoding != "WKB"))
            {
                throw new InvalidDataException("GeoParquet geometry encoding is invalid for the declared version.");
            }

            ValidateGeometryTypes(column.Value);
            var edges = column.Value.TryGetProperty("edges"u8, out _) ? RequiredString(column.Value, "edges") : "planar";
            if (edges is not ("planar" or "spherical"))
            {
                throw new InvalidDataException("GeoParquet 'edges' must be 'planar' or 'spherical'.");
            }

            if (column.Name == primaryColumn)
            {
                encoding = columnEncoding;
                isPlanar = edges == "planar";
                srid = ExtractSrid(column.Value, out crs84Implied);
            }
        }

        if (encoding is null)
        {
            throw new InvalidDataException("GeoParquet primary_column must exactly match an entry in columns.");
        }

        return new GeoParquetMetadata(primaryColumn, encoding, srid, geometryColumnNames.Count,
            geometryColumnNames, crs84Implied, isPlanar);
    }

    private static void ValidateUniqueMetadataProperties(JsonElement element, string[]? knownProperties = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if ((knownProperties is null || knownProperties.Contains(property.Name, StringComparer.Ordinal)) &&
                !names.Add(property.Name))
            {
                throw new InvalidDataException("GeoParquet metadata contains duplicate property names.");
            }
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            throw new InvalidDataException($"GeoParquet metadata has unexpected structure: requires a nonempty '{name}' string.");
        }

        return value.GetString()!;
    }

    private static void ValidateGeometryTypes(JsonElement column)
    {
        if (!column.TryGetProperty("geometry_types"u8, out var types) || types.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GeoParquet metadata requires a 'geometry_types' array.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types.EnumerateArray())
        {
            if (type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("GeoParquet geometry_types entries must be strings.");
            }

            var name = type.GetString()!;
            var baseName = name.EndsWith(" Z", StringComparison.Ordinal) ? name[..^2] : name;
            if (baseName is not ("Point" or "LineString" or "Polygon" or "MultiPoint" or "MultiLineString" or "MultiPolygon" or "GeometryCollection") ||
                !seen.Add(name))
            {
                throw new InvalidDataException("GeoParquet geometry_types entries must be unique supported XY or XYZ geometry types.");
            }
        }
    }

    private static string? GetUnsupportedReason(GeoParquetMetadata metadata) =>
        metadata.Encoding != "WKB" ? UnsupportedEncodingMessage :
        !metadata.IsPlanar ? UnsupportedEdgesMessage :
        metadata.GeometryColumnCount > 1 ? UnsupportedMultipleGeometryMessage : null;

    private static void ThrowIfUnsupported(GeoParquetMetadata metadata)
    {
        if (GetUnsupportedReason(metadata) is { } reason)
        {
            throw new NotSupportedException(reason);
        }
    }

    private static int? ExtractSrid(JsonElement columnElement, out bool crs84Implied)
    {
        crs84Implied = false;
        // GeoParquet 1.1.0 spec: absent "crs" key defaults to OGC:CRS84 (lon-lat).
        // OGC:CRS84 is mapped to SRID 4326 per industry convention (PostGIS, GDAL, etc.)
        // even though EPSG:4326 officially defines lat-lon axis order. The caller
        // surfaces a warning so operators notice the silent semantics mismatch.
        if (!columnElement.TryGetProperty("crs"u8, out var crs))
        {
            crs84Implied = true;
            return 4326;
        }

        // Explicit JSON null means "no CRS associated with this data".
        if (crs.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return GeoParquetCrsResolver.Resolve(crs);
    }

    private sealed record GeoParquetMetadata(
        string PrimaryColumn,
        string Encoding,
        int? Srid,
        int GeometryColumnCount,
        HashSet<string> GeometryColumnNames,
        bool Crs84Implied,
        bool IsPlanar);
}
