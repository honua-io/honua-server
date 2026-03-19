// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;
using ParquetDataColumn = Parquet.Data.DataColumn;

namespace Honua.TestKit;

/// <summary>
/// Shared factory for creating in-memory GeoParquet test files.
/// Eliminates duplication across preview and integration test classes.
/// </summary>
public static class GeoParquetTestFactory
{
    private static readonly string[] PointGeometryTypes = ["Point"];
    private static readonly long?[] SingleObjectId = [1L];
    private static readonly string?[] SingleName = ["Test Feature"];
    private static readonly string?[] SingleKey = ["test_key"];
    private static readonly string?[] SingleValue = ["test_value"];
    private static readonly byte[]?[] NullGeometryData = [null];

    /// <summary>
    /// How the CRS is encoded in the GeoParquet "geo" metadata.
    /// </summary>
    public enum CrsStyle
    {
        /// <summary>PROJJSON format: id.authority = "EPSG", id.code = 4326 (GeoParquet 1.1.0 spec)</summary>
        ProjJson,
        /// <summary>GeoJSON-style: properties.name = "EPSG:4326"</summary>
        PropertiesName,
        /// <summary>No "crs" key — defaults to OGC:CRS84 per spec</summary>
        Omitted,
        /// <summary>PROJJSON format: id.authority = "OGC", id.code = "CRS84"</summary>
        OgcCrs84ProjJson,
        /// <summary>GeoJSON-style: properties.name = "OGC:CRS84"</summary>
        OgcCrs84PropertiesName,
        /// <summary>Explicit JSON null — "no CRS associated with this data" per GeoParquet 1.1.0 spec</summary>
        ExplicitNull,
        /// <summary>PROJJSON format with string code: id.authority = "EPSG", id.code = "4326"</summary>
        ProjJsonStringCode
    }

    /// <summary>
    /// Create a standard GeoParquet file with configurable encoding, CRS, and row count.
    /// </summary>
    public static async Task<MemoryStream> CreateStreamAsync(
        string encoding = "WKB",
        CrsStyle crs = CrsStyle.ProjJson,
        int rowCount = 1,
        bool includeNullGeometryRow = false)
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField);

        var geomColumnMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["encoding"] = encoding,
            ["geometry_types"] = PointGeometryTypes
        };

        if (crs == CrsStyle.ExplicitNull)
        {
            geomColumnMeta["crs"] = null;
        }
        else if (crs != CrsStyle.Omitted)
        {
            geomColumnMeta["crs"] = BuildCrsObject(crs);
        }

        var metadata = BuildGeoMetadata(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["geometry"] = geomColumnMeta
            });

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var totalRows = includeNullGeometryRow ? rowCount + 1 : rowCount;
        var ids = new long?[totalRows];
        var names = new string?[totalRows];
        var geoms = new byte[]?[totalRows];

        for (var i = 0; i < rowCount; i++)
        {
            ids[i] = i + 1L;
            names[i] = rowCount == 1 ? "Test Feature" : $"Feature {i + 1}";
            geoms[i] = geometryBytes;
        }

        if (includeNullGeometryRow)
        {
            ids[totalRows - 1] = totalRows;
            names[totalRows - 1] = "No Geometry";
            geoms[totalRows - 1] = null;
        }

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, ids));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, names));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, geoms));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with a secondary geometry column.
    /// </summary>
    public static async Task<MemoryStream> CreateWithSecondaryGeometryAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var geometry2Field = new DataField<byte[]>("geometry2", true);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField, geometry2Field);

        var metadata = BuildGeoMetadata(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["geometry"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["encoding"] = "WKB",
                    ["geometry_types"] = PointGeometryTypes,
                    ["crs"] = BuildCrsObject(CrsStyle.ProjJson)
                },
                ["geometry2"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["encoding"] = "WKB",
                    ["geometry_types"] = PointGeometryTypes
                }
            });

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new[] { geometryBytes }));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometry2Field, new[] { geometryBytes }));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with a nested StructField column.
    /// </summary>
    public static async Task<MemoryStream> CreateWithNestedColumnAsync(CrsStyle crs = CrsStyle.ProjJson)
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var keyField = new DataField<string>("key", true);
        var valueField = new DataField<string>("value", true);
        var nestedField = new StructField("metadata", keyField, valueField);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField, nestedField);

        var geomColumnMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = PointGeometryTypes,
            ["crs"] = BuildCrsObject(crs)
        };

        var metadata = BuildGeoMetadata(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["geometry"] = geomColumnMeta
            });

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new[] { geometryBytes }));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(keyField, SingleKey));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(valueField, SingleValue));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with TimeOnly and byte[] attribute columns.
    /// Exercises the Parquet types that tripped the import/preview JSON context gap.
    /// </summary>
    public static async Task<MemoryStream> CreateWithTimeAndBinaryColumnsAsync(CrsStyle crs = CrsStyle.ProjJson)
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var timeField = new TimeOnlyDataField("event_time", TimeSpanFormat.MilliSeconds, true);
        var blobField = new DataField<byte[]>("thumbnail", true);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField, timeField, blobField);

        var geomColumnMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = PointGeometryTypes,
            ["crs"] = BuildCrsObject(crs)
        };

        var metadata = BuildGeoMetadata(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["geometry"] = geomColumnMeta
            });

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);
        var sampleBlob = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header fragment

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new[] { geometryBytes }));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(timeField, new TimeOnly?[] { new TimeOnly(14, 30, 0) }));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(blobField, new byte[]?[] { sampleBlob }));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with an INT16 (short) attribute column.
    /// Exercises Parquet integer widths narrower than Int32 that require
    /// explicit JsonSerializable metadata for AOT serialization.
    /// </summary>
    public static async Task<MemoryStream> CreateWithInt16ColumnAsync(CrsStyle crs = CrsStyle.ProjJson)
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var priorityField = new DataField<short?>("priority");
        var schema = new ParquetSchema(objectIdField, nameField, geometryField, priorityField);

        var geomColumnMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = PointGeometryTypes,
            ["crs"] = BuildCrsObject(crs)
        };

        var metadata = BuildGeoMetadata(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["geometry"] = geomColumnMeta
            });

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new[] { geometryBytes }));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(priorityField, new short?[] { 7 }));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a Parquet file with malformed "geo" metadata JSON.
    /// </summary>
    public static async Task<MemoryStream> CreateWithMalformedMetadataAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, geometryField);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = "{{not valid json!!"
        };

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, NullGeometryData));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a Parquet file with structurally invalid "geo" metadata
    /// (valid JSON but wrong value types, e.g. numeric primary_column).
    /// </summary>
    public static async Task<MemoryStream> CreateWithWrongShapedMetadataAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, geometryField);

        // primary_column is a number instead of a string — valid JSON, wrong shape
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                ["primary_column"] = 42,
                ["columns"] = "not_an_object"
            })
        };

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, NullGeometryData));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with valid "geo" JSON but no "primary_column" field.
    /// </summary>
    public static async Task<MemoryStream> CreateWithMissingPrimaryColumnAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, geometryField);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                // No primary_column!
                ["columns"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["geometry"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["encoding"] = "WKB",
                        ["geometry_types"] = PointGeometryTypes
                    }
                }
            })
        };

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, NullGeometryData));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with valid "geo" JSON but no "columns" field.
    /// </summary>
    public static async Task<MemoryStream> CreateWithMissingColumnsFieldAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, geometryField);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                ["primary_column"] = "geometry"
                // No columns!
            })
        };

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, NullGeometryData));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a plain Parquet file without any "geo" metadata key.
    /// </summary>
    public static async Task<MemoryStream> CreateWithoutGeoMetadataAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var schema = new ParquetSchema(objectIdField, nameField);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            // No custom metadata — plain Parquet, not GeoParquet
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file with a raw CRS object for testing edge-case CRS values
    /// (e.g. decimal EPSG codes, out-of-range codes).
    /// </summary>
    public static async Task<MemoryStream> CreateWithCustomCrsAsync(Dictionary<string, object?> crsObject)
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField);

        var geomColumnMeta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = PointGeometryTypes,
            ["crs"] = crsObject
        };

        var metadata = BuildGeoMetadata(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["geometry"] = geomColumnMeta
            });

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new[] { geometryBytes }));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file where the "geo" metadata's primary_column names a column
    /// that does not exist in the Parquet schema.
    /// </summary>
    public static async Task<MemoryStream> CreateWithMismatchedPrimaryColumnAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        // Schema has no "geometry" column — but geo metadata will reference it
        var schema = new ParquetSchema(objectIdField, nameField);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                ["primary_column"] = "geometry",
                ["columns"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["geometry"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["encoding"] = "WKB",
                        ["geometry_types"] = PointGeometryTypes
                    }
                }
            })
        };

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Create a GeoParquet file where "geo.columns" exists but does not contain
    /// the declared "primary_column". The Parquet schema DOES have the geometry column,
    /// so the mismatch is purely in the geo metadata.
    /// </summary>
    public static async Task<MemoryStream> CreateWithPrimaryColumnMissingFromGeoColumnsAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField);

        // primary_column = "geometry" but columns only describes "other_geom"
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                ["primary_column"] = "geometry",
                ["columns"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["other_geom"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["encoding"] = "WKB",
                        ["geometry_types"] = PointGeometryTypes
                    }
                }
            })
        };

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, SingleObjectId));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, SingleName));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new[] { geometryBytes }));
        }

        stream.Position = 0;
        return stream;
    }

    private static Dictionary<string, object?> BuildCrsObject(CrsStyle crs)
    {
        return crs switch
        {
            CrsStyle.ProjJson => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["authority"] = "EPSG",
                    ["code"] = 4326
                }
            },
            CrsStyle.PropertiesName => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "name",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "EPSG:4326"
                }
            },
            CrsStyle.OgcCrs84ProjJson => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["authority"] = "OGC",
                    ["code"] = "CRS84"
                }
            },
            CrsStyle.OgcCrs84PropertiesName => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "name",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = "OGC:CRS84"
                }
            },
            CrsStyle.ProjJsonStringCode => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["authority"] = "EPSG",
                    ["code"] = "4326" // String code per PROJJSON spec
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(crs))
        };
    }

    private static Dictionary<string, string> BuildGeoMetadata(
        Dictionary<string, object?> columns)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                ["primary_column"] = "geometry",
                ["columns"] = columns
            })
        };
    }
}
