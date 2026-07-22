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
/// The LZW and ZSTD benchmarks guard the codecs added in #2854; the DEFLATE,
/// JPEG, and NONE benchmarks are the regression baseline for the paths that
/// shipped before them.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Tile)]
public class TileDecompressorBenchmarks
{
    // A 256x256 grayscale tile (single band, 8bpp) is the modal COG tile
    // shipped by GDAL defaults; size dominates ns/op for zlib so this is
    // a realistic, comparable baseline.
    private const int TileBytes = 256 * 256;

    // The LZW tile is a real GDAL-produced payload (COMPRESS=LZW PREDICTOR=2,
    // 128x128 uint8) rather than a synthetic stream, so the code-width
    // transitions and table churn match what production files exercise.
    private const int LzwTileWidth = 128;

    private byte[] _deflateTile = null!;
    private byte[] _jpegTile = null!;
    private byte[] _rawTile = null!;
    private byte[] _lzwTile = null!;
    private byte[] _zstdTile = null!;
    private TilePixelLayout _lzwLayout;

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

        _rawTile = raw;

        _lzwTile = File.ReadAllBytes(
            Path.Join(AppContext.BaseDirectory, "Fixtures", "lzw-tile-128x128-uint8.bin"));
        _lzwLayout = new TilePixelLayout(
            LzwTileWidth, 1, 8, TilePixelLayout.PredictorHorizontalDifferencing, IsLittleEndian: true);

        using var compressor = new ZstdSharp.Compressor();
        _zstdTile = compressor.Wrap(raw).ToArray();
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

    [Benchmark(Description = "NONE 256x256 byte tile")]
    public byte[] DecompressNone()
    {
        var (data, _) = TileDecompressor.Decompress(_rawTile, "NONE");
        return data;
    }

    [Benchmark(Description = "LZW 128x128 byte tile (GDAL, predictor 2)")]
    public byte[] DecompressLzwWithPredictor()
    {
        var (data, _) = TileDecompressor.Decompress(_lzwTile, "LZW", _lzwLayout);
        return data;
    }

    [Benchmark(Description = "ZSTD 256x256 byte tile")]
    public byte[] DecompressZstd()
    {
        var (data, _) = TileDecompressor.Decompress(_zstdTile, "ZSTD");
        return data;
    }
}
