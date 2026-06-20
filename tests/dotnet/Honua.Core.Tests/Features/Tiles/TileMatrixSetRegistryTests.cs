// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Tests.Features.Tiles;

public class TileMatrixSetRegistryTests
{
    private static CustomTileMatrixSet SampleCustom(string id = "HawaiiAlbers")
        => new()
        {
            Id = id,
            Crs = "http://www.opengis.net/def/crs/EPSG/0/102007",
            Uri = "https://example.com/tms/HawaiiAlbers",
            Title = "Hawaii Albers",
            Srid = 102007,
            TopLeftCorner = [-200000.0, 2400000.0],
            TileWidth = 256,
            TileHeight = 256,
            Levels =
            [
                new TileMatrixLevel { Id = 0, ScaleDenominator = 4000000, CellSize = 1120, MatrixWidth = 1, MatrixHeight = 1 },
                new TileMatrixLevel { Id = 1, ScaleDenominator = 2000000, CellSize = 560, MatrixWidth = 2, MatrixHeight = 2 }
            ]
        };

    [Fact]
    public void All_WithNoCustom_ReturnsTwoBuiltIns()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());

        registry.All.Should().HaveCount(2);
        registry.All.Select(e => e.Id).Should().Equal("WebMercatorQuad", "WorldCRS84Quad");
        registry.All.Should().OnlyContain(e => e.IsBuiltIn);
    }

    [Fact]
    public void All_WithCustom_AppendsCustomAfterBuiltIns()
    {
        var options = new TileMatrixSetDefinitionOptions { Custom = { SampleCustom() } };
        var registry = new TileMatrixSetRegistry(options);

        registry.All.Should().HaveCount(3);
        registry.All.Select(e => e.Id).Should().Equal("WebMercatorQuad", "WorldCRS84Quad", "HawaiiAlbers");
        registry.All[2].IsBuiltIn.Should().BeFalse();
        registry.All[2].Title.Should().Be("Hawaii Albers");
    }

    [Fact]
    public void IsSupported_BuiltInsAndCustom_AreCaseInsensitive()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions { Custom = { SampleCustom() } });

        registry.IsSupported("webmercatorquad").Should().BeTrue();
        registry.IsSupported("WORLDCRS84QUAD").Should().BeTrue();
        registry.IsSupported("hawaiialbers").Should().BeTrue();
        registry.IsSupported("Nope").Should().BeFalse();
    }

    [Fact]
    public void TryGet_WebMercatorQuad_HasProjectedMetadata()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());

        registry.TryGet("WebMercatorQuad", out var entry).Should().BeTrue();
        entry!.Crs.Should().Be("http://www.opengis.net/def/crs/EPSG/0/3857");
        entry.Srid.Should().Be(3857);
        entry.IsGeographic.Should().BeFalse();
    }

    [Fact]
    public void TryGet_WorldCrs84Quad_IsGeographic()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());

        registry.TryGet("WorldCRS84Quad", out var entry).Should().BeTrue();
        entry!.IsGeographic.Should().BeTrue();
        entry.Srid.Should().Be(4326);
    }

    [Fact]
    public void TryGetGeometry_WebMercatorQuad_MatchesCanonicalGrid()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());

        registry.TryGetGeometry("WebMercatorQuad", maxLevel: 3, out var geometry).Should().BeTrue();
        geometry!.Levels.Should().HaveCount(4);
        geometry.TopLeftX.Should().Be(-20037508.342789244);
        geometry.TopLeftY.Should().Be(20037508.342789244);

        // Level 0 is a single 256px tile spanning the whole Web Mercator extent.
        var level0 = geometry.Levels[0];
        level0.MatrixWidth.Should().Be(1);
        level0.MatrixHeight.Should().Be(1);
        level0.ScaleDenominator.Should().BeApproximately(559082264.0287178, 1e-6);

        var bounds = geometry.GetTileBounds(0, 0, 0);
        bounds.Should().NotBeNull();
        bounds!.XMin.Should().BeApproximately(-20037508.342789244, 1e-3);
        bounds.YMax.Should().BeApproximately(20037508.342789244, 1e-3);
    }

    [Fact]
    public void TryGetGeometry_WorldCrs84Quad_IsTwoTilesWideAtZoomZero()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());

        registry.TryGetGeometry("WorldCRS84Quad", maxLevel: 2, out var geometry).Should().BeTrue();
        geometry!.Levels[0].MatrixWidth.Should().Be(2);
        geometry.Levels[0].MatrixHeight.Should().Be(1);
        geometry.TopLeftX.Should().Be(-180.0);
        geometry.TopLeftY.Should().Be(90.0);
    }

    [Fact]
    public void TryGetGeometry_Custom_ReturnsConfiguredLevels()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions { Custom = { SampleCustom() } });

        registry.TryGetGeometry("HawaiiAlbers", maxLevel: 99, out var geometry).Should().BeTrue();
        geometry!.Srid.Should().Be(102007);
        geometry.Levels.Should().HaveCount(2);
        geometry.Levels[0].ScaleDenominator.Should().Be(4000000);
        geometry.TopLeftX.Should().Be(-200000.0);
        geometry.TopLeftY.Should().Be(2400000.0);
    }

    [Fact]
    public void Constructor_ReservedIdCollision_IsIgnoredDefensively()
    {
        // The validator rejects this at startup; the registry must still not throw or
        // shadow the built-in if a collision slips through.
        var options = new TileMatrixSetDefinitionOptions
        {
            Custom = { SampleCustom("WebMercatorQuad") }
        };
        var registry = new TileMatrixSetRegistry(options);

        registry.All.Should().HaveCount(2);
        registry.TryGet("WebMercatorQuad", out var entry).Should().BeTrue();
        entry!.IsBuiltIn.Should().BeTrue();
    }

    // The WMTS GetFeatureInfo handler (#1873) computes the clicked pixel's world
    // coordinate by interpolating (I+0.5)/TileWidth and (J+0.5)/TileHeight across the
    // tile bounds returned by GridGeometry.GetTileBounds. These tests pin that math for
    // both the built-in WebMercatorQuad grid (must stay byte-identical to the legacy
    // inline Web Mercator computation — the CITE guard) and a non-WebMercator grid.
    private static (double X, double Y) PixelCenter(GridGeometry geometry, int col, int row, int level, int i, int j)
    {
        var bounds = geometry.GetTileBounds(col, row, level)!;
        var spanX = bounds.XMax - bounds.XMin;
        var spanY = bounds.YMax - bounds.YMin;
        var x = bounds.XMin + (((i + 0.5) / geometry.TileWidth) * spanX);
        var y = bounds.YMax - (((j + 0.5) / geometry.TileHeight) * spanY);
        return (x, y);
    }

    [Fact]
    public void GridGeometry_PixelCenter_WebMercatorQuad_MatchesLegacyInlineMath()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());
        registry.TryGetGeometry("WebMercatorQuad", maxLevel: 3, out var geometry).Should().BeTrue();

        const double origin = 20037508.342789244;
        const int level = 2;
        const int col = 1;
        const int row = 3;
        const int i = 200;
        const int j = 50;

        // Legacy inline formula previously hardcoded in HandleWmtsGetFeatureInfo.
        var matrixWidth = 2.0 * origin / (1L << level);
        var expectedX = (-origin + col * matrixWidth) + (((i + 0.5) / 256.0) * matrixWidth);
        var expectedY = (origin - row * matrixWidth) - (((j + 0.5) / 256.0) * matrixWidth);

        var (x, y) = PixelCenter(geometry!, col, row, level, i, j);
        x.Should().BeApproximately(expectedX, 1e-6);
        y.Should().BeApproximately(expectedY, 1e-6);
    }

    [Fact]
    public void GridGeometry_PixelCenter_CustomGrid_UsesGridOriginAndCellSize()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions { Custom = { SampleCustom() } });
        registry.TryGetGeometry("HawaiiAlbers", maxLevel: 99, out var geometry).Should().BeTrue();

        // Level 0: single 256px tile, cell size 1120 CRS-units/pixel, origin (-200000, 2400000).
        // The clicked centre pixel maps to origin + (128.5 * 1120) easting and
        // origin - (128.5 * 1120) northing.
        var (x, y) = PixelCenter(geometry!, col: 0, row: 0, level: 0, i: 128, j: 128);
        x.Should().BeApproximately(-200000.0 + (128.5 * 1120.0), 1e-6);
        y.Should().BeApproximately(2400000.0 - (128.5 * 1120.0), 1e-6);
    }

    [Fact]
    public void GridGeometry_FindLevel_MissingLevel_ReturnsNull()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions { Custom = { SampleCustom() } });
        registry.TryGetGeometry("HawaiiAlbers", 0, out var geometry).Should().BeTrue();

        geometry!.FindLevel(0).Should().NotBeNull();
        geometry.FindLevel(9).Should().BeNull();
        geometry.GetTileBounds(0, 0, 9).Should().BeNull();
    }
}
