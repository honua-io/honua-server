// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalOutputGridGuardTests
{
    private static GdalWorkerOptions Options() => new();

    [UnitTest]
    public void TryAdmit_WithinCaps_Admits()
    {
        GdalOutputGridGuard.TryAdmit(4096, 4096, Options(), out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }

    [UnitTest]
    public void TryAdmit_OverWidthCap_Rejects()
    {
        GdalOutputGridGuard.TryAdmit(200_000, 16, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void TryAdmit_OverHeightCap_Rejects()
    {
        GdalOutputGridGuard.TryAdmit(16, 200_000, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterHeight");
    }

    [UnitTest]
    public void TryAdmit_OverPixelCap_Rejects()
    {
        // 40k×40k = 1.6 GP > the default 500 MP cap, but each axis is under the
        // 100k width/height cap, so this trips MaxRasterPixels specifically.
        GdalOutputGridGuard.TryAdmit(40_000, 40_000, Options(), out var error).Should().BeFalse();
        error.Should().Contain("MaxRasterPixels");
    }

    [UnitTest]
    public void TryAdmit_EnormousDimension_Rejects()
    {
        // A pathological Int64 dimension is rejected by the width cap, which trips well
        // before the pixel product is even computed — width/height are int-capped, so the
        // multiply can never overflow Int64 and needs no overflow guard.
        GdalOutputGridGuard.TryAdmit(long.MaxValue, 2, Options(), out var error).Should().BeFalse();
        error.Should().Contain("exceeds configured");
    }

    [UnitTest]
    public void TryAdmit_OverDecodedByteCap_Rejects()
    {
        // Each axis and the pixel product stay under their caps, but the single-band
        // Float64 output (8 bytes/pixel) exceeds MaxDecodedRasterBytes, so the byte cap
        // is what rejects the grid. 30k×30k = 900 MP × 8 = 7.2 GB > the 4 GiB default;
        // raise MaxRasterPixels so the pixel cap does not trip first.
        var options = new GdalWorkerOptions { MaxRasterPixels = long.MaxValue };
        GdalOutputGridGuard.TryAdmit(30_000, 30_000, options, out var error).Should().BeFalse();
        error.Should().Contain("MaxDecodedRasterBytes");
    }

    [UnitTest]
    public void TryAdmit_NonPositive_Rejects()
    {
        GdalOutputGridGuard.TryAdmit(0, 16, Options(), out var error).Should().BeFalse();
        error.Should().Contain("positive");
    }

    // --- resolution-derived (-tr) bound --------------------------------

    [UnitTest]
    public void TryAdmitResolution_WithinCaps_Admits()
    {
        // 10 000-unit extent ÷ 10-unit cell = 1000 px per axis, well under all caps.
        GdalOutputGridGuard.TryAdmitResolution(10_000, 10_000, 10, 10, Options(), out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
    }

    [UnitTest]
    public void TryAdmitResolution_TinyCellOverWideExtent_RejectsOnWidthCap()
    {
        // 1000-unit extent ÷ 0.001-unit cell = 1 000 000 px width > the 100k width cap.
        GdalOutputGridGuard.TryAdmitResolution(1000, 1000, 0.001, 0.001, Options(), out var error)
            .Should().BeFalse();
        error.Should().Contain("output grid width").And.Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void TryAdmitResolution_OverPixelCap_Rejects()
    {
        // 60k×60k derived pixels = 3.6 GP > the 500 MP cap, while each axis (60 000) stays
        // under the 100k width/height caps, so MaxRasterPixels is what trips.
        GdalOutputGridGuard.TryAdmitResolution(60_000, 60_000, 1, 1, Options(), out var error)
            .Should().BeFalse();
        error.Should().Contain("MaxRasterPixels");
    }

    [UnitTest]
    public void TryAdmitResolution_OverDecodedByteCap_Rejects()
    {
        // 30k×30k = 900 MP × 8 bytes (single-band Float64) = 7.2 GB > the 4 GiB byte cap;
        // raise MaxRasterPixels so the pixel cap does not trip first.
        var options = new GdalWorkerOptions { MaxRasterPixels = long.MaxValue };
        GdalOutputGridGuard.TryAdmitResolution(30_000, 30_000, 1, 1, options, out var error)
            .Should().BeFalse();
        error.Should().Contain("MaxDecodedRasterBytes");
    }

    [UnitTest]
    public void TryAdmitResolution_MultiBandFootprintOverDecodedByteCap_Rejects()
    {
        var options = new GdalWorkerOptions
        {
            MaxRasterPixels = long.MaxValue,
            MaxDecodedRasterBytes = 4_000_000_000,
        };

        GdalOutputGridGuard.TryAdmitResolutionWithFootprint(
                20_001,
                20_001,
                1,
                1,
                bytesPerPixel: 10,
                options,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("10 bytes/pixel").And.Contain("MaxDecodedRasterBytes");
    }

    [UnitTest]
    public void TryAdmitResolution_NonFiniteCellSize_Rejects()
    {
        GdalOutputGridGuard.TryAdmitResolution(1000, 1000, double.NaN, 10, Options(), out var error)
            .Should().BeFalse();
        error.Should().Contain("cell size");

        GdalOutputGridGuard.TryAdmitResolution(1000, 1000, 0, 10, Options(), out error)
            .Should().BeFalse();
        error.Should().Contain("cell size");
    }

    [UnitTest]
    public void TryAdmitResolution_DegenerateExtent_Admits()
    {
        // A zero / non-positive extent (single-point or vertical envelope, or a raster
        // with no derivable georeferencing) has nothing to bound on the resolution axis.
        GdalOutputGridGuard.TryAdmitResolution(0, 0, 0.0001, 0.0001, Options(), out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
    }

    // --- reproject & mosaic output-grid bounds -----------------------------

    [UnitTest]
    public void EvaluateReproject_GeographicAtExactPoleToWebMercator_RejectsUnboundedGrid()
    {
        var polarTiff = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 100,
            Height = 100,
            OriginX = -10,
            OriginY = 90,
            ScaleX = 0.1,
            ScaleY = 0.1,
            EpsgCode = 4326,
        });
        var decision = GdalOutputGridGuard.EvaluateReproject(new GdalOutputGridGuard.ReprojectGridCheck
        {
            Source = GdalRasterInput.Inline(polarTiff),
            SourceSrs = "EPSG:4326",
            TargetSrs = "EPSG:3857",
            Options = Options(),
        });

        decision.IsAdmitted.Should().BeFalse();
        decision.Error.Should().Contain("WebMercator").And.Contain("unbounded");
    }

    [UnitTest]
    public void EvaluateReproject_NearPoleSquareGeographicToWebMercator_AdmitsBoundedDiagonalGrid()
    {
        // Matches GDALSuggestedWarpOutput2 diagonal-preserving resolution (~12×1414 px).
        var nearPoleTiff = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 1000,
            Height = 1000,
            OriginX = 0,
            OriginY = 89.9999,
            ScaleX = 0.005,
            ScaleY = 0.005,
            EpsgCode = 4326,
        });
        var decision = GdalOutputGridGuard.EvaluateReproject(new GdalOutputGridGuard.ReprojectGridCheck
        {
            Source = GdalRasterInput.Inline(nearPoleTiff),
            SourceSrs = "EPSG:4326",
            TargetSrs = "EPSG:3857",
            Options = Options(),
        });

        decision.IsAdmitted.Should().BeTrue(decision.Error);
    }

    [UnitTest]
    public void EvaluateReproject_WideAspectGeographicStretchedByWebMercator_RejectsOverPixelCap()
    {
        // 90k×500 = 45 MP input (within caps), but diagonal-preserving WebMercator warp expands to ~1.75 GP > 500 MP cap.
        var wideTiff = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 90_000,
            Height = 500,
            OriginX = 0,
            OriginY = 89.99,
            ScaleX = 0.001,
            ScaleY = 0.02,
            EpsgCode = 4326,
        });
        var decision = GdalOutputGridGuard.EvaluateReproject(new GdalOutputGridGuard.ReprojectGridCheck
        {
            Source = GdalRasterInput.Inline(wideTiff),
            SourceSrs = "EPSG:4326",
            TargetSrs = "EPSG:3857",
            Options = Options(),
        });

        decision.IsAdmitted.Should().BeFalse();
        decision.Error.Should().Contain("MaxRasterPixels");
    }

    [UnitTest]
    public void EvaluateReproject_PolarStereographicCoveringPoleToWebMercator_RejectsUnboundedGrid()
    {
        var polarStereo = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 256,
            Height = 256,
            OriginX = -128_000,
            OriginY = 128_000,
            ScaleX = 1000,
            ScaleY = 1000,
            EpsgCode = 3413,
            CrsKind = TiffHeaderBuilder.GeoTiffCrsKind.Projected,
        });
        var decision = GdalOutputGridGuard.EvaluateReproject(new GdalOutputGridGuard.ReprojectGridCheck
        {
            Source = GdalRasterInput.Inline(polarStereo),
            TargetSrs = "EPSG:3857",
            Options = Options(),
        });

        decision.IsAdmitted.Should().BeFalse();
        decision.Error.Should().Contain("pole").And.Contain("unbounded");
    }

    [UnitTest]
    public void EvaluateReproject_MidLatitudeGeographicToWebMercator_Admits()
    {
        var midLat = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 512,
            Height = 512,
            OriginX = -122.5,
            OriginY = 38.0,
            ScaleX = 0.001,
            ScaleY = 0.001,
            EpsgCode = 4326,
        });
        var decision = GdalOutputGridGuard.EvaluateReproject(new GdalOutputGridGuard.ReprojectGridCheck
        {
            Source = GdalRasterInput.Inline(midLat),
            SourceSrs = "EPSG:4326",
            TargetSrs = "EPSG:3857",
            Options = Options(),
        });

        decision.IsAdmitted.Should().BeTrue(decision.Error);
    }

    [UnitTest]
    public void EvaluateMosaic_SparseTilesFarApart_RejectsOversizedCombinedCanvas()
    {
        var tileA = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 64,
            Height = 64,
            OriginX = 0,
            OriginY = 64,
        });
        var tileB = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 64,
            Height = 64,
            OriginX = 500_000,
            OriginY = 500_064,
        });

        var decision = GdalOutputGridGuard.EvaluateMosaic([tileA, tileB], Options());

        decision.IsAdmitted.Should().BeFalse();
        decision.Error.Should().Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void EvaluateMosaic_MixedResolutionTilesExceedingCap_Rejects()
    {
        var coarse = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 1000,
            Height = 1000,
            OriginX = 0,
            OriginY = 10_000,
            ScaleX = 10.0,
            ScaleY = 10.0,
        });
        var fine = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 100,
            Height = 100,
            OriginX = 0,
            OriginY = 10,
            ScaleX = 0.01,
            ScaleY = 0.01,
        });

        var decision = GdalOutputGridGuard.EvaluateMosaic([coarse, fine], Options());

        decision.IsAdmitted.Should().BeFalse();
        decision.Error.Should().Contain("MaxRasterWidth");
    }

    [UnitTest]
    public void EvaluateMosaic_OverlappingGeographicAndWebMercatorTiles_TransformsIntoCommonCrsAndAdmits()
    {
        var geographicTile = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 100,
            Height = 100,
            OriginX = 0,
            OriginY = 1,
            ScaleX = 0.01,
            ScaleY = 0.01,
            EpsgCode = 4326,
        });
        var mercatorTile = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 100,
            Height = 100,
            OriginX = 0,
            OriginY = 111_325,
            ScaleX = 1_113.2,
            ScaleY = 1_113.2,
            EpsgCode = 3857,
            CrsKind = TiffHeaderBuilder.GeoTiffCrsKind.Projected,
        });

        var decision = GdalOutputGridGuard.EvaluateMosaic([geographicTile, mercatorTile], Options());

        decision.IsAdmitted.Should().BeTrue(decision.Error);
    }

    [UnitTest]
    public void EvaluateMosaic_AdjacentTilesWithinCaps_Admits()
    {
        var tileA = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 128,
            Height = 128,
            OriginX = 0,
            OriginY = 128,
        });
        var tileB = TiffHeaderBuilder.ClassicGeoTiff(new TiffHeaderBuilder.GeoTiffHeaderSpec
        {
            Width = 128,
            Height = 128,
            OriginX = 128,
            OriginY = 128,
        });

        var decision = GdalOutputGridGuard.EvaluateMosaic([tileA, tileB], Options());

        decision.IsAdmitted.Should().BeTrue(decision.Error);
    }
}
