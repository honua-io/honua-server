// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.TestKit.Formats;

/// <summary>
/// Builds well-formed Mapbox Vector Tile 2.1 payloads for tests that need a tile job to carry a
/// real tile rather than four arbitrary bytes (honua-server#4421).
/// </summary>
/// <remarks>
/// Tile-job suites stubbed their providers with <c>[0x01, 0x02, 0x03, 0x04]</c>,
/// <c>Encoding.ASCII.GetBytes("RASTER-TILE-BYTES")</c> and similar, so every "published archive"
/// they validated held payloads that no client could render. Substituting a real tile costs one
/// call here and lets the same assertions decode what came back out — which is the difference
/// between proving an archive has the right shape and proving it carries tiles.
/// </remarks>
public static class MvtTileBuilder
{
    /// <summary>Default vector tile extent.</summary>
    public const int DefaultExtent = 4096;

    /// <summary>
    /// Builds a single-layer tile holding one point feature per supplied coordinate, each carrying
    /// a <c>name</c> attribute.
    /// </summary>
    /// <param name="layerName">Layer name to encode.</param>
    /// <param name="points">Tile-space coordinates and their <c>name</c> attribute values.</param>
    /// <param name="extent">Tile extent; defaults to <see cref="DefaultExtent"/>.</param>
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

        var layer = new MemoryStream();
        WriteTag(layer, 15, 0);           // version
        WriteVarint(layer, 2);
        WriteTag(layer, 1, 2);            // name
        WriteBytes(layer, Encoding.UTF8.GetBytes(layerName));

        foreach (var point in points)
        {
            var feature = new MemoryStream();
            WriteTag(feature, 1, 0);      // id
            WriteVarint(feature, (ulong)(values.IndexOf(point.Name) + 1));
            WriteTag(feature, 2, 2);      // tags (packed)
            var tags = new MemoryStream();
            WriteVarint(tags, 0);                                   // key index: "name"
            WriteVarint(tags, (ulong)values.IndexOf(point.Name));    // value index
            WriteBytes(feature, tags.ToArray());
            WriteTag(feature, 3, 0);      // geometry type
            WriteVarint(feature, 1);      // POINT
            WriteTag(feature, 4, 2);      // geometry (packed)
            var geometry = new MemoryStream();
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
            var encoded = new MemoryStream();
            WriteTag(encoded, 1, 2);      // string_value
            WriteBytes(encoded, Encoding.UTF8.GetBytes(value));
            WriteTag(layer, 4, 2);        // values
            WriteBytes(layer, encoded.ToArray());
        }

        WriteTag(layer, 5, 0);            // extent
        WriteVarint(layer, (ulong)extent);

        var tile = new MemoryStream();
        WriteTag(tile, 3, 2);             // layers
        WriteBytes(tile, layer.ToArray());
        return tile.ToArray();
    }

    /// <summary>
    /// A canonical single-feature tile: layer <c>layer</c>, one point named <c>probe</c> at the
    /// tile's centre. Use where a test needs "a valid tile" and does not care which one.
    /// </summary>
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
