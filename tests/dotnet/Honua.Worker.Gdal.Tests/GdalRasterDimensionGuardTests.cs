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

    // --- positive raster-format allowlist (#2784) ------------------------------

    private static byte[] Jp2CodestreamMagic() => [0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x2F, 0x00, 0x00];

    private static byte[] Jp2BoxMagic() =>
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private static byte[] GifMagic() => "GIF89a"u8.ToArray();

    private static byte[] BmpMagic() => [0x42, 0x4D, 0x36, 0x00, 0x00, 0x00];

    private static byte[] NitfMagic() => "NITF02.10"u8.ToArray();

    private static byte[] HfaMagic() => "EHFA_HEADER_TAG"u8.ToArray();

    [UnitTest]
    public void ClassifyContainer_BombVectorMagicBytes_ReturnsMatchingFormat()
    {
        GdalRasterDimensionGuard.ClassifyContainer(Jp2CodestreamMagic())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Jpeg2000);
        GdalRasterDimensionGuard.ClassifyContainer(Jp2BoxMagic())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Jpeg2000);
        GdalRasterDimensionGuard.ClassifyContainer(GifMagic())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Gif);
        GdalRasterDimensionGuard.ClassifyContainer(BmpMagic())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Bmp);
        GdalRasterDimensionGuard.ClassifyContainer(NitfMagic())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Nitf);
        GdalRasterDimensionGuard.ClassifyContainer(HfaMagic())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Hfa);
    }

    [UnitTest]
    public void ClassifyContainer_GuardedAndUnknownMagicBytes_ReturnsMatchingFormat()
    {
        GdalRasterDimensionGuard.ClassifyContainer(TiffHeaderBuilder.Classic(16, 16, 1, 8))
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Tiff);
        GdalRasterDimensionGuard.ClassifyContainer(TiffHeaderBuilder.Png(16, 16))
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Png);
        GdalRasterDimensionGuard.ClassifyContainer(TiffHeaderBuilder.Jpeg(16, 16, 3))
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Jpeg);
        // A GeoJSON FeatureCollection is not a raster container → Unknown → admitted.
        GdalRasterDimensionGuard.ClassifyContainer("{\"type\":\"FeatureCollection\"}"u8.ToArray())
            .Should().Be(GdalRasterDimensionGuard.RasterContainerFormat.Unknown);
    }

    [UnitTest]
    public void TryAdmit_RefusesNonAllowlistedRasterFormat_Jpeg2000()
    {
        // The strongest live vector: a JP2 whose SIZ could declare dimensions to 2^32,
        // which the dimension guard cannot parse. It must be refused before spawn.
        GdalRasterDimensionGuard.TryAdmit(Jp2CodestreamMagic(), Options(), out var error).Should().BeFalse();
        error.Should().Contain("JPEG2000").And.Contain("allowlist");
    }

    [UnitTest]
    public void TryAdmit_RefusesNonAllowlistedRasterFormats_GifBmpNitfHfa()
    {
        GdalRasterDimensionGuard.TryAdmit(GifMagic(), Options(), out var gif).Should().BeFalse();
        gif.Should().Contain("GIF");
        GdalRasterDimensionGuard.TryAdmit(BmpMagic(), Options(), out var bmp).Should().BeFalse();
        bmp.Should().Contain("BMP");
        GdalRasterDimensionGuard.TryAdmit(NitfMagic(), Options(), out var nitf).Should().BeFalse();
        nitf.Should().Contain("NITF");
        GdalRasterDimensionGuard.TryAdmit(HfaMagic(), Options(), out var hfa).Should().BeFalse();
        hfa.Should().Contain("HFA");
    }

    [UnitTest]
    public void TryAdmit_StillAdmitsAllowlistedFormats()
    {
        // Regression: the allowlist must not disturb the dimension-guarded formats.
        GdalRasterDimensionGuard.TryAdmit(TiffHeaderBuilder.Classic(512, 512, 3, 8), Options(), out _).Should().BeTrue();
        GdalRasterDimensionGuard.TryAdmit(TiffHeaderBuilder.Png(256, 256, colourType: 6), Options(), out _).Should().BeTrue();
        GdalRasterDimensionGuard.TryAdmit(TiffHeaderBuilder.Jpeg(800, 600, 3), Options(), out _).Should().BeTrue();
    }

    [UnitTest]
    public void TryAdmit_ExtendedAllowlist_AdmitsFormat_ExplicitOptIn()
    {
        // Operators can extend the allowlist (accepting the documented OOM risk); once
        // JPEG2000 is allowed, the guard admits its magic (bounding then falls to GDAL).
        var options = new GdalWorkerOptions
        {
            AllowedRasterInputFormats = { "JPEG2000" },
        };
        GdalRasterDimensionGuard.TryAdmit(Jp2CodestreamMagic(), options, out var error).Should().BeTrue(error);
    }

    // --- GeoTIFF ModelPixelScale read (resample -tr extent, #2793) -------------

    [UnitTest]
    public void ReadGeoTiffPixelScale_ReadsModelPixelScale_FromClassicTiff()
    {
        var tiff = TiffHeaderBuilder.ClassicWithPixelScale(width: 1024, height: 512, scaleX: 30.0, scaleY: 20.0);

        GdalRasterDimensionGuard.ReadGeoTiffPixelScale(tiff, out var scaleX, out var scaleY);

        scaleX.Should().Be(30.0);
        scaleY.Should().Be(20.0);
    }

    [UnitTest]
    public void ReadGeoTiffPixelScale_BigEndianModelPixelScale_IsRead()
    {
        var tiff = TiffHeaderBuilder.ClassicWithPixelScale(
            width: 100, height: 100, scaleX: 0.5, scaleY: 0.25, littleEndian: false);

        GdalRasterDimensionGuard.ReadGeoTiffPixelScale(tiff, out var scaleX, out var scaleY);

        scaleX.Should().Be(0.5);
        scaleY.Should().Be(0.25);
    }

    [UnitTest]
    public void ReadGeoTiffPixelScale_NoGeoreferencing_DefaultsToIdentity()
    {
        // A TIFF without a ModelPixelScale tag has no georeferencing; the extent bound
        // then measures the extent in pixels (identity 1.0), matching gdalwarp -tr.
        var tiff = TiffHeaderBuilder.Classic(width: 1024, height: 512);

        GdalRasterDimensionGuard.ReadGeoTiffPixelScale(tiff, out var scaleX, out var scaleY);

        scaleX.Should().Be(1.0);
        scaleY.Should().Be(1.0);
    }

    [UnitTest]
    public void ReadGeoTiffPixelScale_NonTiffPayload_DefaultsToIdentity()
    {
        var png = TiffHeaderBuilder.Png(width: 800, height: 600);

        GdalRasterDimensionGuard.ReadGeoTiffPixelScale(png, out var scaleX, out var scaleY);

        scaleX.Should().Be(1.0);
        scaleY.Should().Be(1.0);
    }
}
