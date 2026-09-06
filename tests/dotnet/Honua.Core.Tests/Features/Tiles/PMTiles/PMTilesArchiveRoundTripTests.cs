// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Core.Tests.Features.Tiles.PMTiles;

/// <summary>
/// Archive-level PMTiles evidence: gzip-compressed real MVT tiles, matched by z/x/y, resolved
/// through a leaf directory (honua-server#4397).
/// </summary>
/// <remarks>
/// <para>
/// Before this file no test decompressed a <see cref="PMTilesCompression.Gzip"/> tile, no test
/// exercised the leaf-directory path (every round trip fit in the root directory), and the one
/// end-to-end round trip matched tiles to their expected bytes <em>by payload length</em> — so a
/// writer that placed every tile at the wrong coordinate would have passed as long as the lengths
/// happened to be distinct.
/// </para>
/// <para>
/// The tile payloads here are real Mapbox Vector Tiles, encoded and decoded by the small
/// protobuf helpers at the bottom of this file rather than by any code under test, so "the tile
/// survived the archive" means the layer name, extent and point coordinates survived — not that
/// an opaque blob round-tripped.
/// </para>
/// </remarks>
public class PMTilesArchiveRoundTripTests
{
    [Fact]
    public async Task WriteAsync_GzipCompressedMvtTiles_DecompressToTheirOwnCoordinatesTile()
    {
        var tiles = new (int Z, int X, int Y)[]
        {
            (0, 0, 0),
            (1, 0, 0),
            (1, 1, 0),
            (1, 0, 1),
            (2, 3, 1),
        };

        // Tile compression is Gzip: the archive stores deflate streams, not the payloads, so a
        // reader that skips decompression cannot match the expectation below.
        var writer = new PMTilesWriter(PMTilesCompression.Gzip, PMTilesCompression.Gzip);
        foreach (var (z, x, y) in tiles)
        {
            writer.AddTile(z, x, y, MiniMvt.Encode($"layer-{z}-{x}-{y}", featureX: 100 + x, featureY: 200 + y));
        }

        var archive = await WriteArchiveAsync(writer, minZoom: 0, maxZoom: 2);
        var header = PMTilesHeader.ReadFrom(archive);
        header.TileCompression.Should().Be(PMTilesCompression.Gzip);

        var reader = new ArchiveReader(archive, header);

        foreach (var (z, x, y) in tiles)
        {
            // Resolve by coordinate, not by scanning for a matching length.
            var stored = reader.ReadTile(z, x, y);
            stored.Should().NotBeNull("tile {0}/{1}/{2} must be addressable in the archive", z, x, y);

            // The stored bytes must genuinely be gzip: decompressing is what makes them readable.
            stored![..2].Should().Equal([0x1F, 0x8B], "gzip-compressed tiles carry the gzip magic");

            var decompressed = PMTilesDirectory.Decompress(stored, header.TileCompression);
            var decoded = MiniMvt.Decode(decompressed);
            decoded.LayerName.Should().Be($"layer-{z}-{x}-{y}",
                "the tile served for {0}/{1}/{2} must be that tile, not a neighbour", z, x, y);
            decoded.Extent.Should().Be(4096);
            decoded.PointX.Should().Be(100 + x);
            decoded.PointY.Should().Be(200 + y);
        }
    }

    [Fact]
    public async Task WriteAsync_ArchiveLargerThanTheRootDirectory_ResolvesTilesThroughALeafDirectory()
    {
        // PMTilesDirectory.BuildDirectories only considers the single-root path when the entry
        // count is at or below its 16384 cap, so 16512 entries takes the leaf-directory branch
        // deterministically rather than depending on how well the root happens to compress.
        // That branch had never been exercised by any test.
        const int zoom = 8;
        const int xCount = 129;
        const int yCount = 128;
        var writer = new PMTilesWriter(PMTilesCompression.Gzip, PMTilesCompression.Gzip);
        for (var x = 0; x < xCount; x++)
        {
            for (var y = 0; y < yCount; y++)
            {
                writer.AddTile(zoom, x, y, MiniMvt.Encode($"l{x}-{y}", featureX: x, featureY: y));
            }
        }

        writer.TileCount.Should().BeGreaterThan(
            16384, "the leaf-directory branch is only taken past the root entry cap");

        var archive = await WriteArchiveAsync(writer, minZoom: zoom, maxZoom: zoom);
        var header = PMTilesHeader.ReadFrom(archive);

        header.LeafDirectoryLength.Should().BeGreaterThan(
            0, "an archive this size must spill into at least one leaf directory");

        var reader = new ArchiveReader(archive, header);

        // Tiles from both ends of the Hilbert ordering: reachable only by following a root
        // pointer into a leaf directory.
        foreach (var (x, y) in new[] { (0, 0), (63, 64), (128, 127) })
        {
            var stored = reader.ReadTile(zoom, x, y);
            stored.Should().NotBeNull("tile {0}/{1}/{2} must resolve through the leaf directory", zoom, x, y);

            var decoded = MiniMvt.Decode(PMTilesDirectory.Decompress(stored!, header.TileCompression));
            decoded.LayerName.Should().Be($"l{x}-{y}");
            decoded.PointX.Should().Be(x);
            decoded.PointY.Should().Be(y);
        }

        // And a coordinate that was never added must not resolve to some other tile's bytes.
        // (A lower zoom, because every tile at `zoom` was added; an out-of-range x at `zoom`
        // would be rejected by HilbertCurve rather than reaching the directory.)
        reader.ReadTile(zoom - 1, 0, 0).Should().BeNull();
    }

    private static async Task<byte[]> WriteArchiveAsync(PMTilesWriter writer, byte minZoom, byte maxZoom)
    {
        var metadata = new PMTilesArchiveMetadata
        {
            MinLon = -180,
            MinLat = -85,
            MaxLon = 180,
            MaxLat = 85,
            MinZoom = minZoom,
            MaxZoom = maxZoom
        };

        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, metadata);
        return stream.ToArray();
    }

    /// <summary>
    /// Resolves a z/x/y coordinate to its tile bytes by walking the archive exactly as an
    /// independent PMTiles reader does: header, root directory, then a leaf directory when the
    /// matching root entry is a pointer (run length 0).
    /// </summary>
    private sealed class ArchiveReader
    {
        private readonly byte[] _archive;
        private readonly PMTilesHeader _header;
        private readonly PMTilesEntry[] _root;

        public ArchiveReader(byte[] archive, PMTilesHeader header)
        {
            _archive = archive;
            _header = header;
            _root = ReadDirectory(header.RootDirectoryOffset, header.RootDirectoryLength);
        }

        public byte[]? ReadTile(int z, int x, int y)
        {
            var tileId = HilbertCurve.XYZToTileId(z, x, y);

            var entry = Find(_root, tileId);
            if (entry is null)
            {
                return null;
            }

            if (entry.Value.RunLength == 0)
            {
                // Root pointer into a leaf directory.
                var leaf = ReadDirectory(_header.LeafDirectoryOffset + entry.Value.Offset, entry.Value.Length);
                entry = Find(leaf, tileId);
                if (entry is null || entry.Value.RunLength == 0)
                {
                    return null;
                }
            }

            var offset = checked((int)(_header.TileDataOffset + entry.Value.Offset));
            return _archive.AsSpan(offset, checked((int)entry.Value.Length)).ToArray();
        }

        private PMTilesEntry[] ReadDirectory(ulong offset, ulong length)
        {
            var raw = _archive.AsSpan(checked((int)offset), checked((int)length)).ToArray();
            return PMTilesDirectory.DeserializeEntries(
                PMTilesDirectory.Decompress(raw, _header.InternalCompression));
        }

        private static PMTilesEntry? Find(PMTilesEntry[] entries, ulong tileId)
        {
            // Entries are sorted by tile id; a run covers [TileId, TileId + RunLength).
            PMTilesEntry? candidate = null;
            foreach (var entry in entries)
            {
                if (entry.TileId > tileId)
                {
                    break;
                }

                candidate = entry;
            }

            if (candidate is null)
            {
                return null;
            }

            if (candidate.Value.RunLength == 0)
            {
                // Leaf pointer: its run is the id range up to the next root entry.
                return candidate;
            }

            return tileId < candidate.Value.TileId + candidate.Value.RunLength ? candidate : null;
        }
    }

    /// <summary>
    /// A minimal Mapbox Vector Tile encoder/decoder, written against the published
    /// <c>vector_tile.proto</c> wire format and sharing no code with anything under test. It emits
    /// one layer holding one point feature, which is enough for "did this exact tile survive the
    /// archive?" to be answerable from the decoded content.
    /// </summary>
    private static class MiniMvt
    {
        public static byte[] Encode(string layerName, int featureX, int featureY)
        {
            // Feature { id = 1, type = POINT(1), geometry = [MoveTo(1), zigzag(x), zigzag(y)] }
            var feature = new MemoryStream();
            WriteVarintField(feature, fieldNumber: 1, value: 1);                 // id
            WriteVarintField(feature, fieldNumber: 3, value: 1);                 // GeomType.POINT
            var geometry = new MemoryStream();
            WriteVarint(geometry, (1 << 3) | 1);                                 // MoveTo, count 1
            WriteVarint(geometry, ZigZag(featureX));
            WriteVarint(geometry, ZigZag(featureY));
            WriteLengthDelimitedField(feature, fieldNumber: 4, geometry.ToArray());

            // Layer { name, features, extent, version }
            var layer = new MemoryStream();
            WriteLengthDelimitedField(layer, fieldNumber: 1, Encoding.UTF8.GetBytes(layerName));
            WriteLengthDelimitedField(layer, fieldNumber: 2, feature.ToArray());
            WriteVarintField(layer, fieldNumber: 5, value: 4096);                // extent
            WriteVarintField(layer, fieldNumber: 15, value: 2);                  // version

            // Tile { layers }
            var tile = new MemoryStream();
            WriteLengthDelimitedField(tile, fieldNumber: 3, layer.ToArray());
            return tile.ToArray();
        }

        public static DecodedTile Decode(byte[] payload)
        {
            var span = new ReadOnlySpan<byte>(payload);
            var layerBytes = ReadFirstLengthDelimited(span, fieldNumber: 3);
            layerBytes.IsEmpty.Should().BeFalse("an MVT tile must carry at least one layer");

            string? name = null;
            uint extent = 4096;
            int pointX = 0, pointY = 0;

            var cursor = 0;
            while (cursor < layerBytes.Length)
            {
                var key = ReadVarint(layerBytes, ref cursor);
                var field = (int)(key >> 3);
                var wireType = (int)(key & 0x7);

                if (wireType == 2)
                {
                    var length = (int)ReadVarint(layerBytes, ref cursor);
                    var value = layerBytes.Slice(cursor, length);
                    cursor += length;

                    if (field == 1)
                    {
                        name = Encoding.UTF8.GetString(value);
                    }
                    else if (field == 2)
                    {
                        (pointX, pointY) = DecodeFeaturePoint(value);
                    }
                }
                else if (wireType == 0)
                {
                    var value = ReadVarint(layerBytes, ref cursor);
                    if (field == 5)
                    {
                        extent = (uint)value;
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected MVT wire type {wireType} for field {field}.");
                }
            }

            return new DecodedTile(name, extent, pointX, pointY);
        }

        private static (int X, int Y) DecodeFeaturePoint(ReadOnlySpan<byte> feature)
        {
            var cursor = 0;
            while (cursor < feature.Length)
            {
                var key = ReadVarint(feature, ref cursor);
                var field = (int)(key >> 3);
                var wireType = (int)(key & 0x7);

                if (wireType == 0)
                {
                    _ = ReadVarint(feature, ref cursor);
                    continue;
                }

                if (wireType != 2)
                {
                    throw new InvalidOperationException($"Unexpected MVT feature wire type {wireType}.");
                }

                var length = (int)ReadVarint(feature, ref cursor);
                var value = feature.Slice(cursor, length);
                cursor += length;

                if (field != 4)
                {
                    continue;
                }

                var geometryCursor = 0;
                var command = ReadVarint(value, ref geometryCursor);
                (command & 0x7).Should().Be(1UL, "the encoded geometry must start with a MoveTo");
                var x = UnZigZag(ReadVarint(value, ref geometryCursor));
                var y = UnZigZag(ReadVarint(value, ref geometryCursor));
                return (x, y);
            }

            throw new InvalidOperationException("MVT feature carried no geometry field.");
        }

        private static ReadOnlySpan<byte> ReadFirstLengthDelimited(ReadOnlySpan<byte> buffer, int fieldNumber)
        {
            var cursor = 0;
            while (cursor < buffer.Length)
            {
                var key = ReadVarint(buffer, ref cursor);
                var field = (int)(key >> 3);
                var wireType = (int)(key & 0x7);

                if (wireType != 2)
                {
                    throw new InvalidOperationException($"Unexpected MVT tile wire type {wireType}.");
                }

                var length = (int)ReadVarint(buffer, ref cursor);
                if (field == fieldNumber)
                {
                    return buffer.Slice(cursor, length);
                }

                cursor += length;
            }

            return [];
        }

        private static void WriteVarintField(Stream output, int fieldNumber, ulong value)
        {
            WriteVarint(output, (ulong)(fieldNumber << 3));
            WriteVarint(output, value);
        }

        private static void WriteLengthDelimitedField(Stream output, int fieldNumber, byte[] value)
        {
            WriteVarint(output, (ulong)((fieldNumber << 3) | 2));
            WriteVarint(output, (ulong)value.Length);
            output.Write(value, 0, value.Length);
        }

        private static void WriteVarint(Stream output, ulong value)
        {
            while (value >= 0x80)
            {
                output.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            output.WriteByte((byte)value);
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> buffer, ref int cursor)
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                var current = buffer[cursor++];
                result |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }
        }

        private static ulong ZigZag(int value) => (ulong)((value << 1) ^ (value >> 31));

        private static int UnZigZag(ulong value) => (int)((value >> 1) ^ (ulong)(-(long)(value & 1)));

        public readonly record struct DecodedTile(string? LayerName, uint Extent, int PointX, int PointY);
    }
}
