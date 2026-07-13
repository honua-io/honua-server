// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Unit coverage for <see cref="GdalOutputGridGuard"/> — the pre-spawn bound on the
/// attacker-controlled OUTPUT grid of the rasterize / interpolate executors (#2782).
/// Proves an over-cap requested output size is rejected with a clear message before
/// any GDAL allocation, reusing the same caps as the input dimension guard.
/// </summary>
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

    // --- resolution-derived (-tr) bound (#2793) --------------------------------

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
}
