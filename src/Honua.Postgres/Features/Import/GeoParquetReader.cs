// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Streaming GeoParquet format reader. Reads GeoParquet files using Parquet.Net,
/// extracting WKB geometry and attribute columns into NTS features.
/// </summary>
internal static class GeoParquetReader
{
    private const int YieldInterval = 256;

    /// <summary>
    /// Maximum rows allowed in a single Parquet row group before the import/preview
    /// paths reject the file. Parquet.Net materializes an entire row group's column
    /// data in memory, so unbounded row groups defeat the streaming memory contract.
    /// </summary>
    internal const long MaxRowsPerRowGroup = 100_000;

    /// <summary>
    /// Error message used when a GeoParquet file uses a non-WKB geometry encoding.
    /// Shared between the import and preview rejection paths.
    /// </summary>
    internal const string UnsupportedEncodingMessage =
        "GeoParquet file uses an unsupported geometry encoding. Only WKB encoding is supported in this version.";

    /// <summary>
    /// Builds a rejection message for files containing a row group that exceeds the
    /// bounded-memory threshold. Shared between the import and preview paths.
    /// </summary>
    internal static string BuildLargeRowGroupMessage(long maxRowGroupRows, int rowGroupCount) =>
        $"GeoParquet file has a row group containing {maxRowGroupRows:N0} rows across {rowGroupCount} row group(s), " +
        $"exceeding the {MaxRowsPerRowGroup:N0}-row per-row-group limit. Re-export the file with smaller row groups " +
        "to enable bounded-memory import.";

    /// <summary>
    /// Streams features from a GeoParquet file. The stream must be seekable.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("GeoParquet reader requires a seekable stream.");
        }

        stream.Position = 0;
        using var reader = await CreateReaderAsync(stream, cancellationToken);
        var geoMeta = ParseGeoMetadata(reader.CustomMetadata);
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
                if (string.Equals(df.Name, primaryColumn, StringComparison.OrdinalIgnoreCase))
                {
                    geometryFieldIndex = i;
                }
                else if (!geometryColumnNames.Contains(df.Name))
                {
                    attributeFields.Add((i, df));
                }
            }
            // Nested types (StructField, ListField, MapField) are not DataFields — warned via DetectWarnings
        }

        if (geometryFieldIndex < 0)
        {
            throw new InvalidDataException($"GeoParquet geometry column '{primaryColumn}' not found in schema.");
        }

        // Parquet is a columnar format — Parquet.Net reads all rows of a column within a
        // row group in a single ReadColumnAsync call. This means each row group's data is
        // materialized in memory before features are yielded. Well-partitioned files use
        // many small row groups and work within the import service's bounded-memory contract.
        // Files with a single large row group (e.g. Honua's own exporter) will spike memory
        // proportional to the row group size. See follow-on to bound the exporter's row groups.
        var recordCount = 0;
        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var rowGroupReader = reader.OpenRowGroupReader(rg);

            // Enforce the per-row-group ceiling even when ExtractMetadataAsync was
            // bypassed (it is advisory only). Parquet.Net materializes the whole
            // row group on ReadColumnAsync, so an oversized group defeats the
            // streaming-memory contract.
            if (rowGroupReader.RowCount > MaxRowsPerRowGroup)
            {
                throw new InvalidDataException(
                    BuildLargeRowGroupMessage(rowGroupReader.RowCount, reader.RowGroupCount));
            }

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

                NetTopologySuite.Geometries.Geometry? geometry = null;
                var wkbBytes = geometryData[row];
                if (wkbBytes != null)
                {
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
        var isWkbEncoding = string.Equals(geoMeta.Encoding, "WKB", StringComparison.OrdinalIgnoreCase);

        var warnings = new List<string>();

        // Check encoding
        if (!isWkbEncoding)
        {
            warnings.Add($"GeoParquet file uses '{geoMeta.Encoding}' geometry encoding. Only WKB encoding is supported.");
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
            warnings.Add($"GeoParquet file contains multiple geometry columns. Only the primary column '{geoMeta.PrimaryColumn}' will be imported.");
        }

        // Check for nested types in schema
        var schema = reader.Schema;
        foreach (var field in schema.Fields)
        {
            if (field is StructField sf)
            {
                warnings.Add($"Column '{sf.Name}' uses a nested type (Struct) that cannot be imported. This column will be skipped.");
            }
            else if (field is ListField lf)
            {
                warnings.Add($"Column '{lf.Name}' uses a nested type (List) that cannot be imported. This column will be skipped.");
            }
            else if (field is MapField mf)
            {
                warnings.Add($"Column '{mf.Name}' uses a nested type (Map) that cannot be imported. This column will be skipped.");
            }
        }

        // Verify primary geometry column exists in the Parquet schema as a DataField.
        // Fail fast here instead of deferring to ReadStreamingAsync, which would read
        // attribute columns before discovering the mismatch.
        var hasPrimaryColumn = false;
        foreach (var field in schema.Fields)
        {
            if (field is DataField df &&
                string.Equals(df.Name, geoMeta.PrimaryColumn, StringComparison.OrdinalIgnoreCase))
            {
                hasPrimaryColumn = true;
                break;
            }
        }

        if (!hasPrimaryColumn)
        {
            throw new InvalidDataException(
                $"GeoParquet geometry column '{geoMeta.PrimaryColumn}' not found in Parquet schema.");
        }

        var totalRowCount = reader.Metadata?.NumRows ?? 0;
        var rowGroupCount = reader.RowGroupCount;

        // Determine the largest row group — Parquet.Net materializes an entire
        // row group's columns in memory, so any oversized group defeats the
        // streaming memory contract. OpenRowGroupReader only reads metadata
        // from the already-loaded footer; no column data is touched here.
        long maxRowGroupRows = 0;
        for (var rg = 0; rg < rowGroupCount; rg++)
        {
            using var rgReader = reader.OpenRowGroupReader(rg);
            if (rgReader.RowCount > maxRowGroupRows)
            {
                maxRowGroupRows = rgReader.RowCount;
            }
        }

        stream.Position = 0;
        return new GeoParquetFileMetadata(geoMeta.Srid, isWkbEncoding, warnings.ToArray(), totalRowCount, rowGroupCount, maxRowGroupRows);
    }

    /// <summary>
    /// Import-relevant metadata extracted from a GeoParquet file in a single reader pass.
    /// </summary>
    internal sealed record GeoParquetFileMetadata(
        int? Srid,
        bool IsWkbEncoding,
        string[] Warnings,
        long TotalRowCount,
        int RowGroupCount,
        long MaxRowGroupRowCount);

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

    private static GeoParquetMetadata ParseGeoMetadata(IReadOnlyDictionary<string, string>? customMetadata)
    {
        if (customMetadata == null || !customMetadata.TryGetValue("geo", out var geoJson))
        {
            throw new InvalidDataException("GeoParquet file is missing required 'geo' metadata key.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(geoJson);
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

        // primary_column is required per GeoParquet 1.0.0+ spec
        if (!root.TryGetProperty("primary_column", out var pc) ||
            pc.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "GeoParquet 'geo' metadata has unexpected structure: required 'primary_column' field is missing or not a string.");
        }

        var primaryColumn = pc.GetString()!;

        // columns is required per GeoParquet 1.0.0+ spec
        if (!root.TryGetProperty("columns", out var columns) ||
            columns.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "GeoParquet 'geo' metadata has unexpected structure: required 'columns' field is missing or not an object.");
        }

        string? encoding = null;
        int? srid = null;
        var geometryColumnCount = 0;
        var geometryColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundPrimaryInColumns = false;
        var crs84Implied = false;

        foreach (var col in columns.EnumerateObject())
        {
            geometryColumnCount++;
            geometryColumnNames.Add(col.Name);

            if (!string.Equals(col.Name, primaryColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foundPrimaryInColumns = true;
            var colObj = col.Value;

            // Encoding
            if (colObj.TryGetProperty("encoding", out var enc))
            {
                encoding = enc.GetString();
            }

            // CRS detection — priority order per design brief
            srid = ExtractSrid(colObj, out crs84Implied);
        }

        // The GeoParquet spec requires that primary_column references a key in "columns".
        // Accepting a missing entry would silently default to WKB with no SRID, masking
        // a malformed file instead of surfacing a clear error.
        if (!foundPrimaryInColumns)
        {
            throw new InvalidDataException(
                $"GeoParquet 'geo' metadata declares primary_column '{primaryColumn}' but 'columns' does not contain a matching entry.");
        }

        return new GeoParquetMetadata(primaryColumn, encoding ?? "WKB", srid, geometryColumnCount, geometryColumnNames, crs84Implied);
    }

    private static int? ExtractSrid(JsonElement columnElement, out bool crs84Implied)
    {
        crs84Implied = false;
        // GeoParquet 1.1.0 spec: absent "crs" key defaults to OGC:CRS84 (lon-lat).
        // OGC:CRS84 is mapped to SRID 4326 per industry convention (PostGIS, GDAL, etc.)
        // even though EPSG:4326 officially defines lat-lon axis order. The caller
        // surfaces a warning so operators notice the silent semantics mismatch.
        if (!columnElement.TryGetProperty("crs", out var crs))
        {
            crs84Implied = true;
            return 4326;
        }

        // Explicit JSON null means "no CRS associated with this data".
        if (crs.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Format 1: PROJJSON with id.authority + id.code (GeoParquet 1.1.0 spec)
        if (crs.TryGetProperty("id", out var id) &&
            id.TryGetProperty("authority", out var authority) &&
            id.TryGetProperty("code", out var code))
        {
            var authorityStr = authority.GetString();

            // EPSG authority — PROJJSON permits both numeric and string codes.
            // Use TryGetInt32/TryParse to gracefully handle non-integral (4326.5) or
            // out-of-range values instead of throwing FormatException/OverflowException.
            if (string.Equals(authorityStr, "EPSG", StringComparison.OrdinalIgnoreCase))
            {
                if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var epsgCode))
                {
                    return epsgCode;
                }

                if (code.ValueKind == JsonValueKind.String &&
                    int.TryParse(code.GetString(), out var epsgCodeFromString))
                {
                    return epsgCodeFromString;
                }
            }

            // OGC:CRS84 → SRID 4326 per industry convention (lon-lat, same as PostGIS/GDAL)
            if (string.Equals(authorityStr, "OGC", StringComparison.OrdinalIgnoreCase) &&
                code.ValueKind == JsonValueKind.String &&
                string.Equals(code.GetString(), "CRS84", StringComparison.OrdinalIgnoreCase))
            {
                return 4326;
            }
        }

        // Format 2: GeoJSON-style crs.properties.name (Honua export format)
        if (crs.TryGetProperty("properties", out var props) &&
            props.TryGetProperty("name", out var name))
        {
            var nameStr = name.GetString();
            if (nameStr != null)
            {
                // EPSG:<code> (e.g. "EPSG:4326")
                if (nameStr.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(nameStr.AsSpan(5), out var epsgCode))
                {
                    return epsgCode;
                }

                // OGC:CRS84 → SRID 4326
                if (string.Equals(nameStr, "OGC:CRS84", StringComparison.OrdinalIgnoreCase))
                {
                    return 4326;
                }
            }
        }

        return null;
    }

    private sealed record GeoParquetMetadata(
        string PrimaryColumn,
        string Encoding,
        int? Srid,
        int GeometryColumnCount,
        HashSet<string> GeometryColumnNames,
        bool Crs84Implied);
}
