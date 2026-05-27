// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using Honua.Core.Features.Raster.CogParser;

namespace Honua.Benchmarks;

/// <summary>
/// Measures <see cref="TileDecompressor.Decompress"/> on a representative
/// DEFLATE-compressed COG tile payload. The JPEG passthrough path is also
/// measured because <c>[Params]</c> over the compression code mirrors real
/// COG distributions where producers ship a mix of codecs.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Tile)]
public class TileDecompressorBenchmarks
{
    // A 256x256 grayscale tile (single band, 8bpp) is the modal COG tile
    // shipped by GDAL defaults; size dominates ns/op for zlib so this is
    // a realistic, comparable baseline.
    private const int TileBytes = 256 * 256;

    private byte[] _deflateTile = null!;
    private byte[] _jpegTile = null!;

    [GlobalSetup]
    public void Setup()
    {
        var raw = new byte[TileBytes];
        // Use a deterministic but slightly compressible pattern so the
        // deflate-decode path exercises both literal and length/distance
        // codes — pure zero-fill collapses to a few bytes and is not
        // representative of real raster payloads.
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)((i * 31) ^ (i >> 3));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        _deflateTile = compressed.ToArray();

        // JPEG hits the zero-copy passthrough branch; we still synthesize
        // a plausible byte buffer (a JFIF SOI marker prefix) so the
        // benchmark exercises the dispatch + tuple allocation, not file I/O.
        _jpegTile = new byte[8 * 1024];
        _jpegTile[0] = 0xFF;
        _jpegTile[1] = 0xD8;
        _jpegTile[2] = 0xFF;
        _jpegTile[3] = 0xE0;
    }

    [Benchmark(Baseline = true, Description = "DEFLATE 256x256 byte tile")]
    public byte[] DecompressDeflate()
    {
        var (data, _) = TileDecompressor.Decompress(_deflateTile, "DEFLATE");
        return data;
    }

    [Benchmark(Description = "JPEG passthrough")]
    public byte[] DecompressJpegPassthrough()
    {
        var (data, _) = TileDecompressor.Decompress(_jpegTile, "JPEG");
        return data;
    }
}
