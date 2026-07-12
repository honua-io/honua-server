// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Unit coverage for <see cref="GdalRasterDimensionGuard"/> — the pre-processing
/// pixel-dimension admission control (#2766). Exercises the cheap TIFF-header
/// dimension read AND the pure cap evaluation directly, with NO GDAL binary, so
/// the decompression-bomb reject is proven to happen from a header read before any
/// full-raster allocation.
/// </summary>
public sealed class GdalRasterDimensionGuardTests
{
    private static GdalWorkerOptions Options() => new();

    // --- header parsing --------------------------------------------------------

    [UnitTest]
    public void ReadsDimensions_FromClassicLittleEndianTiffHeader()
    {
        var tiff = TiffHeaderBuilder.Classic(width: 640, height: 480, bands: 3, bits: 16, littleEndian: true);

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Width.Should().Be(640);
        dims.Height.Should().Be(480);
        dims.Bands.Should().Be(3);
        dims.BitsPerSample.Should().Be(16);
        dims.PixelCount.Should().Be(640L * 480L);
        dims.EstimatedDecodedBytes.Should().Be(640L * 480L * 3 * 2);
    }

    [UnitTest]
    public void ReadsDimensions_FromClassicBigEndianTiffHeader()
    {
        var tiff = TiffHeaderBuilder.Classic(width: 1024, height: 768, bands: 1, bits: 8, littleEndian: false);

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Width.Should().Be(1024);
        dims.Height.Should().Be(768);
        dims.Bands.Should().Be(1);
    }

    [UnitTest]
    public void ReadsDimensions_FromBigTiffHeader()
    {
        var tiff = TiffHeaderBuilder.BigTiff(width: 200_000, height: 200_000, bands: 4, bits: 8, littleEndian: true);

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(tiff, out var dims).Should().BeTrue();
        dims.Width.Should().Be(200_000);
        dims.Height.Should().Be(200_000);
        dims.Bands.Should().Be(4);
    }

    [UnitTest]
    public void UnrecognizedBytes_AreUndetermined_AndAdmitted()
    {
        // Not a TIFF, PNG, or JPEG magic → cannot bound cheaply → admit.
        var unknown = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 };

        GdalRasterDimensionGuard.TryReadRasterDimensions(unknown, out _).Should().BeFalse();
        GdalRasterDimensionGuard.TryAdmit(unknown, Options(), out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }

    // --- PNG / JPEG header parsing (non-TIFF bomb vectors) ----------------------

    [UnitTest]
    public void ReadsDimensions_FromPngHeader()
    {
        var png = TiffHeaderBuilder.Png(width: 640, height: 480, bitDepth: 8, colourType: 2);

        GdalRasterDimensionGuard.TryReadRasterDimensions(png, out var dims).Should().BeTrue();
        dims.Width.Should().Be(640);
        dims.Height.Should().Be(480);
        dims.Bands.Should().Be(3, "PNG colour type 2 is truecolour RGB");
    }

    [UnitTest]
    public void TryAdmit_RejectsHugeDimensionPng()
    {
        // The exact non-TIFF vector called out in review: a tiny PNG whose IHDR
        // declares a 100000×100000 canvas (10^10 pixels).
        var bomb = TiffHeaderBuilder.Png(width: 100_000, height: 100_000, colourType: 2);
        bomb.Length.Should().BeLessThan(64);

        GdalRasterDimensionGuard.TryAdmit(bomb, Options(), out var error).Should().BeFalse();
        error.Should().Contain("exceeds configured");
    }

    [UnitTest]
    public void ReadsDimensions_FromJpegHeader_SkippingApp0()
    {
        var jpeg = TiffHeaderBuilder.Jpeg(width: 800, height: 600, components: 3);

        GdalRasterDimensionGuard.TryReadRasterDimensions(jpeg, out var dims).Should().BeTrue();
        dims.Width.Should().Be(800);
        dims.Height.Should().Be(600);
        dims.Bands.Should().Be(3);
    }

    [UnitTest]
    public void TryAdmit_RejectsHugeDimensionJpeg()
    {
        // JPEG maxes at 65535 per axis, but 65535×65535×3 ≈ 12.8 GB decoded.
        var bomb = TiffHeaderBuilder.Jpeg(width: 65_535, height: 65_535, components: 3);

        GdalRasterDimensionGuard.TryAdmit(bomb, Options(), out var error).Should().BeFalse();
        error.Should().Contain("exceeds configured");
    }

    [UnitTest]
    public void TryAdmit_AdmitsNormalPng()
    {
        var png = TiffHeaderBuilder.Png(width: 256, height: 256, colourType: 6);
        GdalRasterDimensionGuard.TryAdmit(png, Options(), out _).Should().BeTrue();
    }

    // --- BigTIFF LONG8 overflow must fail closed (reject, not admit) ------------

    [UnitTest]
    public void TryAdmit_RejectsBigTiffDimensionOverInt64Max()
    {
        // A BigTIFF whose LONG8 width exceeds Int64.Max would wrap negative on a
        // naive cast and be silently admitted; the guard clamps and rejects.
        var overflow = TiffHeaderBuilder.BigTiffUnsigned(width: ulong.MaxValue, height: 1024, bands: 1, bits: 8);

        GdalRasterDimensionGuard.TryReadRasterDimensions(overflow, out var dims).Should().BeTrue();
        dims.Width.Should().Be(long.MaxValue, "the > Int64.Max width is clamped to a positive absurd value");
        GdalRasterDimensionGuard.TryAdmit(overflow, Options(), out var error).Should().BeFalse();
        error.Should().Contain("exceeds configured");
    }

    // --- pure cap evaluation ---------------------------------------------------

    [UnitTest]
    public void WithinLimits_Admits()
    {
        var dims = new GdalRasterDimensionGuard.RasterDimensions(10_000, 10_000, 4, 8);
        GdalRasterDimensionGuard.IsWithinLimits(dims, Options(), out _).Should().BeTrue();
    }

    [UnitTest]
    public void OverPixelCap_Rejects()
    {
        // 40k×40k = 1.6 GP > default 500 MP cap, but each dimension is under the
        // 100k width/height cap, so this trips MaxRasterPixels specifically.
        var dims = new GdalRasterDimensionGuard.RasterDimensions(40_000, 40_000, 1, 8);
        GdalRasterDimensionGuard.IsWithinLimits(dims, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterPixels");
    }

    [UnitTest]
    public void OverWidthCap_Rejects()
    {
        var dims = new GdalRasterDimensionGuard.RasterDimensions(200_000, 1, 1, 8);
        GdalRasterDimensionGuard.IsWithinLimits(dims, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void OverHeightCap_Rejects()
    {
        var dims = new GdalRasterDimensionGuard.RasterDimensions(1, 200_000, 1, 8);
        GdalRasterDimensionGuard.IsWithinLimits(dims, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterHeight");
    }

    [UnitTest]
    public void OverBandCap_Rejects()
    {
        var dims = new GdalRasterDimensionGuard.RasterDimensions(16, 16, 1024, 8);
        GdalRasterDimensionGuard.IsWithinLimits(dims, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterBands");
    }

    [UnitTest]
    public void OverDecodedByteCap_Rejects()
    {
        // 20k×20k = 400 MP (under the 500 MP cap) and under width/height caps,
        // but Float64 (8 bytes) makes it ~3.2 GB decoded — over the 4 GiB default?
        // 400M × 8 = 3.2 GB < 4 GiB, so bump bands to 2 → 6.4 GB > 4 GiB.
        var dims = new GdalRasterDimensionGuard.RasterDimensions(20_000, 20_000, 2, 64);
        GdalRasterDimensionGuard.IsWithinLimits(dims, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxDecodedRasterBytes");
    }

    // --- end-to-end guard on a bomb-declaring header ---------------------------

    [UnitTest]
    public void TryAdmit_RejectsCompressibleBombHeader()
    {
        // A ~60-byte TIFF header that DECLARES a 1,000,000 × 1,000,000 raster
        // (10^12 pixels): the exact decompression-bomb shape. The guard reads the
        // header and rejects without materializing anything.
        var bomb = TiffHeaderBuilder.Classic(width: 1_000_000, height: 1_000_000, bands: 1, bits: 8);
        bomb.Length.Should().BeLessThan(256, "the bomb is tiny on disk; only its declared dimensions are huge");

        GdalRasterDimensionGuard.TryAdmit(bomb, Options(), out var error).Should().BeFalse();
        error.Should().Contain("exceeds configured");
    }

    [UnitTest]
    public void TryAdmit_AdmitsNormalRasterHeader()
    {
        var normal = TiffHeaderBuilder.Classic(width: 512, height: 512, bands: 3, bits: 8);
        GdalRasterDimensionGuard.TryAdmit(normal, Options(), out _).Should().BeTrue();
    }
}
