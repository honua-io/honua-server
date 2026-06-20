// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Honua.Core.Tests.Raster.ZarrParser;

/// <summary>
/// Builds synthetic Zarr v2 store layouts for tests. Each fixture is returned as
/// a path-to-bytes dictionary suitable for <see cref="InMemoryZarrRangeReader"/>.
/// </summary>
internal static class ZarrFixtureBuilder
{
    public static Dictionary<string, byte[]> BuildSingleVariableUncompressed(
        string root,
        int rows,
        int cols,
        int chunkRows,
        int chunkCols,
        Func<int, int, float> sample)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var zarray = new StringBuilder();
        zarray.Append('{')
            .Append("\"chunks\":[").Append(chunkRows).Append(',').Append(chunkCols).Append("],")
            .Append("\"compressor\":null,")
            .Append("\"dtype\":\"<f4\",")
            .Append("\"fill_value\":0,")
            .Append("\"filters\":null,")
            .Append("\"order\":\"C\",")
            .Append("\"shape\":[").Append(rows).Append(',').Append(cols).Append("],")
            .Append("\"zarr_format\":2}");
        objects[root + "/.zarray"] = Encoding.UTF8.GetBytes(zarray.ToString());

        AppendChunksFloat32(root, rows, cols, chunkRows, chunkCols, sample, compress: false, objects);
        return objects;
    }

    public static Dictionary<string, byte[]> BuildGroupedZlib(
        string root,
        int rows,
        int cols,
        int chunkRows,
        int chunkCols,
        Func<int, int, float> sample,
        int srid,
        double xMin,
        double yMin,
        double xMax,
        double yMax)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        objects[root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}");
        var attrs = "{"
            + "\"variables\":[\"temperature\"],"
            + "\"primary_variable\":\"temperature\","
            + "\"crs_wkid\":" + srid + ","
            + "\"extent\":[" +
                xMin.ToString("R", CultureInfo.InvariantCulture) + "," +
                yMin.ToString("R", CultureInfo.InvariantCulture) + "," +
                xMax.ToString("R", CultureInfo.InvariantCulture) + "," +
                yMax.ToString("R", CultureInfo.InvariantCulture) + "],"
            + "\"x_dimension\":\"x\","
            + "\"y_dimension\":\"y\""
            + "}";
        objects[root + "/.zattrs"] = Encoding.UTF8.GetBytes(attrs);

        var arrayRoot = root + "/temperature";
        var zarray = "{"
            + "\"chunks\":[" + chunkRows + "," + chunkCols + "],"
            + "\"compressor\":{\"id\":\"zlib\",\"level\":1},"
            + "\"dtype\":\"<f4\","
            + "\"fill_value\":\"NaN\","
            + "\"filters\":null,"
            + "\"order\":\"C\","
            + "\"shape\":[" + rows + "," + cols + "],"
            + "\"zarr_format\":2}";
        objects[arrayRoot + "/.zarray"] = Encoding.UTF8.GetBytes(zarray);
        var arrayAttrs = "{\"_ARRAY_DIMENSIONS\":[\"y\",\"x\"]}";
        objects[arrayRoot + "/.zattrs"] = Encoding.UTF8.GetBytes(arrayAttrs);

        AppendChunksFloat32(arrayRoot, rows, cols, chunkRows, chunkCols, sample, compress: true, objects);
        return objects;
    }

    /// <summary>
    /// Builds a grouped store with a 3D (time, y, x) variable and optional
    /// temporal <c>.zattrs</c> keys. When <paramref name="includeTemporalAttrs"/>
    /// is false the time-axis attributes are omitted so the extractor reports a
    /// null temporal extent. Chunk payloads are not written (metadata-only fixture).
    /// </summary>
    public static Dictionary<string, byte[]> BuildGroupedWithTime(
        string root,
        int timeSteps,
        int rows,
        int cols,
        bool includeTemporalAttrs)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        objects[root + "/.zgroup"] = Encoding.UTF8.GetBytes("{\"zarr_format\":2}");

        var attrs = new StringBuilder();
        attrs.Append('{')
            .Append("\"variables\":[\"temperature\"],")
            .Append("\"primary_variable\":\"temperature\",")
            .Append("\"crs_wkid\":4326,")
            .Append("\"extent\":[-180,-90,180,90],")
            .Append("\"x_dimension\":\"x\",")
            .Append("\"y_dimension\":\"y\",")
            .Append("\"t_dimension\":\"time\"");
        if (includeTemporalAttrs)
        {
            attrs.Append(",\"t_start\":\"2026-01-01T00:00:00Z\"")
                .Append(",\"t_end\":\"2026-01-05T00:00:00Z\"")
                .Append(",\"t_step_seconds\":86400");
        }
        attrs.Append('}');
        objects[root + "/.zattrs"] = Encoding.UTF8.GetBytes(attrs.ToString());

        var arrayRoot = root + "/temperature";
        var zarray = "{"
            + "\"chunks\":[" + timeSteps + "," + rows + "," + cols + "],"
            + "\"compressor\":null,"
            + "\"dtype\":\"<f4\","
            + "\"fill_value\":0,"
            + "\"filters\":null,"
            + "\"order\":\"C\","
            + "\"shape\":[" + timeSteps + "," + rows + "," + cols + "],"
            + "\"zarr_format\":2}";
        objects[arrayRoot + "/.zarray"] = Encoding.UTF8.GetBytes(zarray);
        objects[arrayRoot + "/.zattrs"] = Encoding.UTF8.GetBytes("{\"_ARRAY_DIMENSIONS\":[\"time\",\"y\",\"x\"]}");
        return objects;
    }

    /// <summary>
    /// Builds a Zarr v3 single-array store (root <c>zarr.json</c> with
    /// <c>node_type: array</c>), little-endian float32, the default
    /// <c>c/</c>-prefixed chunk key encoding, optionally gzip-coded chunks.
    /// </summary>
    public static Dictionary<string, byte[]> BuildV3SingleArray(
        string root,
        int rows,
        int cols,
        int chunkRows,
        int chunkCols,
        Func<int, int, float> sample,
        bool gzip)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var codecs = gzip
            ? "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"gzip\",\"configuration\":{\"level\":5}}]"
            : "[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}]";
        var json = "{"
            + "\"zarr_format\":3,"
            + "\"node_type\":\"array\","
            + "\"shape\":[" + rows + "," + cols + "],"
            + "\"data_type\":\"float32\","
            + "\"chunk_grid\":{\"name\":\"regular\",\"configuration\":{\"chunk_shape\":[" + chunkRows + "," + chunkCols + "]}},"
            + "\"chunk_key_encoding\":{\"name\":\"default\",\"configuration\":{\"separator\":\"/\"}},"
            + "\"fill_value\":0,"
            + "\"codecs\":" + codecs + ","
            + "\"dimension_names\":[\"y\",\"x\"]"
            + "}";
        objects[root + "/zarr.json"] = Encoding.UTF8.GetBytes(json);

        AppendV3ChunksFloat32(root, rows, cols, chunkRows, chunkCols, sample, gzip, separator: "/", objects);
        return objects;
    }

    /// <summary>
    /// Builds a Zarr v3 group store (root <c>zarr.json</c> with
    /// <c>node_type: group</c> and a <c>variables</c> attribute manifest) holding a
    /// single georeferenced float32 array.
    /// </summary>
    public static Dictionary<string, byte[]> BuildV3Group(
        string root,
        int rows,
        int cols,
        int chunkRows,
        int chunkCols,
        Func<int, int, float> sample,
        int srid,
        double xMin,
        double yMin,
        double xMax,
        double yMax)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var groupJson = "{"
            + "\"zarr_format\":3,"
            + "\"node_type\":\"group\","
            + "\"attributes\":{"
                + "\"variables\":[\"temperature\"],"
                + "\"primary_variable\":\"temperature\","
                + "\"crs_wkid\":" + srid + ","
                + "\"extent\":[" +
                    xMin.ToString("R", CultureInfo.InvariantCulture) + "," +
                    yMin.ToString("R", CultureInfo.InvariantCulture) + "," +
                    xMax.ToString("R", CultureInfo.InvariantCulture) + "," +
                    yMax.ToString("R", CultureInfo.InvariantCulture) + "],"
                + "\"x_dimension\":\"x\","
                + "\"y_dimension\":\"y\""
            + "}}";
        objects[root + "/zarr.json"] = Encoding.UTF8.GetBytes(groupJson);

        var arrayRoot = root + "/temperature";
        var arrayJson = "{"
            + "\"zarr_format\":3,"
            + "\"node_type\":\"array\","
            + "\"shape\":[" + rows + "," + cols + "],"
            + "\"data_type\":\"float32\","
            + "\"chunk_grid\":{\"name\":\"regular\",\"configuration\":{\"chunk_shape\":[" + chunkRows + "," + chunkCols + "]}},"
            + "\"chunk_key_encoding\":{\"name\":\"default\",\"configuration\":{\"separator\":\"/\"}},"
            + "\"fill_value\":0,"
            + "\"codecs\":[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}}],"
            + "\"dimension_names\":[\"y\",\"x\"]"
            + "}";
        objects[arrayRoot + "/zarr.json"] = Encoding.UTF8.GetBytes(arrayJson);

        AppendV3ChunksFloat32(arrayRoot, rows, cols, chunkRows, chunkCols, sample, gzip: false, separator: "/", objects);
        return objects;
    }

    /// <summary>
    /// Builds a Zarr v3 single array declaring an unsupported codec (blosc) so the
    /// reader's codec-pipeline gating can be asserted.
    /// </summary>
    public static Dictionary<string, byte[]> BuildV3UnsupportedCodec(string root)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var json = "{"
            + "\"zarr_format\":3,"
            + "\"node_type\":\"array\","
            + "\"shape\":[4,4],"
            + "\"data_type\":\"float32\","
            + "\"chunk_grid\":{\"name\":\"regular\",\"configuration\":{\"chunk_shape\":[4,4]}},"
            + "\"chunk_key_encoding\":{\"name\":\"default\",\"configuration\":{\"separator\":\"/\"}},"
            + "\"fill_value\":0,"
            + "\"codecs\":[{\"name\":\"bytes\",\"configuration\":{\"endian\":\"little\"}},{\"name\":\"blosc\",\"configuration\":{}}],"
            + "\"dimension_names\":[\"y\",\"x\"]"
            + "}";
        objects[root + "/zarr.json"] = Encoding.UTF8.GetBytes(json);
        return objects;
    }

    private static void AppendV3ChunksFloat32(
        string arrayRoot,
        int rows,
        int cols,
        int chunkRows,
        int chunkCols,
        Func<int, int, float> sample,
        bool gzip,
        string separator,
        Dictionary<string, byte[]> objects)
    {
        var chunkRowCount = (rows + chunkRows - 1) / chunkRows;
        var chunkColCount = (cols + chunkCols - 1) / chunkCols;
        for (var cr = 0; cr < chunkRowCount; cr++)
        {
            for (var cc = 0; cc < chunkColCount; cc++)
            {
                var startRow = cr * chunkRows;
                var startCol = cc * chunkCols;
                var raw = new byte[chunkRows * chunkCols * sizeof(float)];
                for (var r = 0; r < chunkRows; r++)
                {
                    for (var c = 0; c < chunkCols; c++)
                    {
                        var globalRow = startRow + r;
                        var globalCol = startCol + c;
                        var value = (globalRow < rows && globalCol < cols) ? sample(globalRow, globalCol) : 0f;
                        var offset = (r * chunkCols + c) * sizeof(float);
                        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, raw, offset, sizeof(float));
                    }
                }

                // v3 default chunk key encoding: "c" + separator + dotted indices.
                var chunkKey = arrayRoot + "/c" + separator
                    + cr.ToString(CultureInfo.InvariantCulture) + separator
                    + cc.ToString(CultureInfo.InvariantCulture);
                if (gzip)
                {
                    using var output = new MemoryStream();
                    using (var deflate = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(raw, 0, raw.Length);
                    }
                    objects[chunkKey] = output.ToArray();
                }
                else
                {
                    objects[chunkKey] = raw;
                }
            }
        }
    }

    public static Dictionary<string, byte[]> BuildInvalidJson(string root)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        objects[root + "/.zarray"] = Encoding.UTF8.GetBytes("{not-json");
        return objects;
    }

    public static Dictionary<string, byte[]> BuildUnsupportedV3(string root)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        objects[root + "/.zarray"] = Encoding.UTF8.GetBytes("{\"chunks\":[1],\"shape\":[1],\"dtype\":\"<f4\",\"order\":\"C\",\"compressor\":null,\"fill_value\":0,\"zarr_format\":3}");
        return objects;
    }

    public static Dictionary<string, byte[]> BuildFortranOrder(string root)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        objects[root + "/.zarray"] = Encoding.UTF8.GetBytes("{\"chunks\":[1],\"shape\":[1],\"dtype\":\"<f4\",\"order\":\"F\",\"compressor\":null,\"fill_value\":0,\"zarr_format\":2}");
        return objects;
    }

    public static Dictionary<string, byte[]> BuildFilteredArray(string root)
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        objects[root + "/.zarray"] = Encoding.UTF8.GetBytes("{\"chunks\":[1],\"shape\":[1],\"dtype\":\"<f4\",\"order\":\"C\",\"compressor\":null,\"fill_value\":0,\"filters\":[{\"id\":\"delta\",\"dtype\":\"<f4\"}],\"zarr_format\":2}");
        return objects;
    }

    private static void AppendChunksFloat32(
        string arrayRoot,
        int rows,
        int cols,
        int chunkRows,
        int chunkCols,
        Func<int, int, float> sample,
        bool compress,
        Dictionary<string, byte[]> objects)
    {
        var chunkRowCount = (rows + chunkRows - 1) / chunkRows;
        var chunkColCount = (cols + chunkCols - 1) / chunkCols;
        for (var cr = 0; cr < chunkRowCount; cr++)
        {
            for (var cc = 0; cc < chunkColCount; cc++)
            {
                var startRow = cr * chunkRows;
                var startCol = cc * chunkCols;
                var raw = new byte[chunkRows * chunkCols * sizeof(float)];
                for (var r = 0; r < chunkRows; r++)
                {
                    for (var c = 0; c < chunkCols; c++)
                    {
                        var globalRow = startRow + r;
                        var globalCol = startCol + c;
                        var value = (globalRow < rows && globalCol < cols) ? sample(globalRow, globalCol) : 0f;
                        var offset = (r * chunkCols + c) * sizeof(float);
                        var bytes = BitConverter.GetBytes(value);
                        Buffer.BlockCopy(bytes, 0, raw, offset, sizeof(float));
                    }
                }

                var chunkKey = arrayRoot + "/" + cr.ToString(CultureInfo.InvariantCulture) + "." + cc.ToString(CultureInfo.InvariantCulture);
                if (compress)
                {
                    using var output = new MemoryStream();
                    using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(raw, 0, raw.Length);
                    }
                    objects[chunkKey] = output.ToArray();
                }
                else
                {
                    objects[chunkKey] = raw;
                }
            }
        }
    }
}
