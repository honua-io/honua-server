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
    public void NonTiffBytes_AreUndetermined_AndAdmitted()
    {
        var notTiff = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0, 1, 2, 3, 4 };

        GdalRasterDimensionGuard.TryReadGeoTiffDimensions(notTiff, out _).Should().BeFalse();
        // Undetermined header ⇒ admit (GDAL adjudicates), not reject.
        GdalRasterDimensionGuard.TryAdmit(notTiff, Options(), out var error).Should().BeTrue();
        error.Should().BeEmpty();
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
