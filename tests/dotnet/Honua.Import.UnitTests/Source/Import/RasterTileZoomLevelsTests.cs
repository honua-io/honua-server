// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Import.RasterImport;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Raster import zoom-level parsing (#6177). An out-of-range token is a 400,
/// not an unhandled overflow.
/// </summary>
public sealed class RasterTileZoomLevelsTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void TryParseTileZoomLevels_OverflowToken_IsClientError()
    {
        var ok = RasterImportEndpoints.TryParseTileZoomLevels(
            "99999999999",
            out _,
            out var error);

        ok.Should().BeFalse();
        error.Should().Be("'tileZoomLevels' must be a comma-separated list of integers (0-24).");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("1,99999999999")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void TryParseTileZoomLevels_UnparseableToken_IsClientError(string zoom)
    {
        var ok = RasterImportEndpoints.TryParseTileZoomLevels(zoom, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Be("'tileZoomLevels' must be a comma-separated list of integers (0-24).");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TryParseTileZoomLevels_OutOfRange_NamesTheRange()
    {
        var ok = RasterImportEndpoints.TryParseTileZoomLevels("0,25", out _, out var error);

        ok.Should().BeFalse();
        error.Should().Be("'tileZoomLevels' values must be between 0 and 24.");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TryParseTileZoomLevels_ValidList_DeduplicatesAndSorts()
    {
        var ok = RasterImportEndpoints.TryParseTileZoomLevels("3,1,3", out var levels, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        levels.Should().Equal(1, 3);
    }
}
