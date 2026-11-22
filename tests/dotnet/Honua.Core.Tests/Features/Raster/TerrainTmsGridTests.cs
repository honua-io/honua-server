// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

public sealed class TerrainTmsGridTests
{
    [UnitTest]
    public void ValidateTileCoordinates_EnforcesGeographicTmsMatrixBounds()
    {
        TerrainTmsGrid.ValidateTileCoordinates(0, 0, 0).Should().BeTrue();
        TerrainTmsGrid.ValidateTileCoordinates(0, 1, 0).Should().BeTrue();
        TerrainTmsGrid.ValidateTileCoordinates(0, 2, 0).Should().BeFalse();
        TerrainTmsGrid.ValidateTileCoordinates(0, 0, 1).Should().BeFalse();
        TerrainTmsGrid.ValidateTileCoordinates(1, 3, 1).Should().BeTrue();
        TerrainTmsGrid.ValidateTileCoordinates(1, 4, 1).Should().BeFalse();
        TerrainTmsGrid.ValidateTileCoordinates(-1, 0, 0).Should().BeFalse();
    }

    [UnitTest]
    public void GetTileBounds_UsesSouthToNorthTmsRowOrder()
    {
        var westRoot = TerrainTmsGrid.GetTileBounds(0, 0, 0);
        westRoot.Should().Be(new TerrainGeographicBounds { West = -180.0, South = -90.0, East = 0.0, North = 90.0 });

        var eastRoot = TerrainTmsGrid.GetTileBounds(0, 1, 0);
        eastRoot.Should().Be(new TerrainGeographicBounds { West = 0.0, South = -90.0, East = 180.0, North = 90.0 });

        var southRow = TerrainTmsGrid.GetTileBounds(1, 0, 0);
        southRow.Should().Be(new TerrainGeographicBounds { West = -180.0, South = -90.0, East = -90.0, North = 0.0 });

        var northRow = TerrainTmsGrid.GetTileBounds(1, 0, 1);
        northRow.Should().Be(new TerrainGeographicBounds { West = -180.0, South = 0.0, East = -90.0, North = 90.0 });
    }

    [UnitTest]
    public void ComputeAvailableRanges_ComputesBoundedTmsRangesPerZoomLevel()
    {
        var mauiBounds = new[] { -156.7, 20.5, -155.9, 21.1 };
        var ranges = TerrainTmsGrid.ComputeAvailableRanges(mauiBounds, minZoom: 0, maxZoom: 3);

        ranges.Should().HaveCount(4);
        ranges[0].Should().ContainSingle().Which.Should().Be(new TerrainTileRange { StartX = 0, StartY = 0, EndX = 0, EndY = 0 });
        ranges[1].Should().ContainSingle().Which.Should().Be(new TerrainTileRange { StartX = 0, StartY = 1, EndX = 0, EndY = 1 });
        ranges[2].Should().ContainSingle().Which.Should().Be(new TerrainTileRange { StartX = 0, StartY = 2, EndX = 0, EndY = 2 });
        ranges[3].Should().ContainSingle().Which.Should().Be(new TerrainTileRange { StartX = 1, StartY = 4, EndX = 1, EndY = 4 });
    }

    [UnitTest]
    public void ComputeAvailableRanges_WithNullOrInvalidBounds_ReturnsEmpty()
    {
        TerrainTmsGrid.ComputeAvailableRanges(null, 0, 2).Should().BeEmpty();
        TerrainTmsGrid.ComputeAvailableRanges([10.0, 0.0, -10.0, 10.0], 0, 2).Should().BeEmpty();
    }
}
