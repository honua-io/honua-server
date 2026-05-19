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
