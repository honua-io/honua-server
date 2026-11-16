// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.TestKit.Formats;

public static class MvtTileBuilder
{
    public const int DefaultExtent = 4096;

    public static byte[] PointLayer(
        string layerName,
        IReadOnlyList<(int X, int Y, string Name)> points,
        int extent = DefaultExtent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A vector tile layer needs at least one feature.", nameof(points));
        }

        var keys = new List<string> { "name" };
        var values = points.Select(point => point.Name).Distinct(StringComparer.Ordinal).ToList();

        using var layer = new MemoryStream();
        WriteTag(layer, 15, 0);           // version
        WriteVarint(layer, 2);
        WriteTag(layer, 1, 2);            // name
        WriteBytes(layer, Encoding.UTF8.GetBytes(layerName));

        foreach (var point in points)
        {
            using var feature = new MemoryStream();
            WriteTag(feature, 1, 0);      // id
            WriteVarint(feature, (ulong)(values.IndexOf(point.Name) + 1));
            WriteTag(feature, 2, 2);      // tags (packed)
            using var tags = new MemoryStream();
            WriteVarint(tags, 0);                                   // key index: "name"
            WriteVarint(tags, (ulong)values.IndexOf(point.Name));    // value index
            WriteBytes(feature, tags.ToArray());
            WriteTag(feature, 3, 0);      // geometry type
            WriteVarint(feature, 1);      // POINT
            WriteTag(feature, 4, 2);      // geometry (packed)
            using var geometry = new MemoryStream();
            WriteVarint(geometry, (1 & 0x7) | (1 << 3));             // MoveTo, count 1
            WriteVarint(geometry, ZigZag(point.X));
            WriteVarint(geometry, ZigZag(point.Y));
            WriteBytes(feature, geometry.ToArray());

            WriteTag(layer, 2, 2);        // features
            WriteBytes(layer, feature.ToArray());
        }

        foreach (var key in keys)
        {
            WriteTag(layer, 3, 2);        // keys
            WriteBytes(layer, Encoding.UTF8.GetBytes(key));
        }

        foreach (var value in values)
        {
            using var encoded = new MemoryStream();
            WriteTag(encoded, 1, 2);      // string_value
            WriteBytes(encoded, Encoding.UTF8.GetBytes(value));
            WriteTag(layer, 4, 2);        // values
            WriteBytes(layer, encoded.ToArray());
        }

        WriteTag(layer, 5, 0);            // extent
        WriteVarint(layer, (ulong)extent);

        using var tile = new MemoryStream();
        WriteTag(tile, 3, 2);             // layers
        WriteBytes(tile, layer.ToArray());
        return tile.ToArray();
    }

    public static byte[] Canonical()
        => PointLayer("layer", [(DefaultExtent / 2, DefaultExtent / 2, "probe")]);

    private static void WriteTag(Stream stream, int fieldNumber, int wireType)
        => WriteVarint(stream, ((ulong)fieldNumber << 3) | (uint)wireType);

    private static void WriteBytes(Stream stream, byte[] payload)
    {
        WriteVarint(stream, (ulong)payload.Length);
        stream.Write(payload);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static ulong ZigZag(int value) => (ulong)((value << 1) ^ (value >> 31));
}
