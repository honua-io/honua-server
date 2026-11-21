// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.GeoServices;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

[Protocol(TestProtocols.MapServer)]
public sealed class ExportTileGridTests
{
    private static readonly double[] World =
        [-180d, -SpatialConstants.WebMercatorMaxLatitude, 180d, SpatialConstants.WebMercatorMaxLatitude];

    [UnitTest]
    [Operation(Operations.Export)]
    public void Count_WholeWorldAtEveryLevel_IsExactWithoutBuildingTheGrid()
    {
        var levels = Enumerable.Range(0, 19).ToArray();

        // Sum of 4^z for z = 0..18.
        ExportTileGrid.Count(World, levels).Should().Be(91_625_968_981L);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_StopsAtTheCapInLevelOrder()
    {
        var tiles = ExportTileGrid.Build(World, [0, 1, 18], maxTiles: 7);

        tiles.Should().HaveCount(7);
        tiles[0].Should().Be(new ExportTileCoordinate(0, 0, 0));
        tiles.Skip(1).Take(4).Should().OnlyContain(tile => tile.Z == 1);
        tiles.Skip(5).Should().OnlyContain(tile => tile.Z == 18);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_WithinTheCap_ReturnsEveryTileOfTheExtent()
    {
        double[] quarter = [1d, 1d, 179d, 84d];

        var tiles = ExportTileGrid.Build(quarter, [2], maxTiles: 64);

        ExportTileGrid.Count(quarter, [2]).Should().Be(4);
        tiles.Should().BeEquivalentTo(
            [
                new ExportTileCoordinate(2, 2, 0),
                new ExportTileCoordinate(2, 2, 1),
                new ExportTileCoordinate(2, 3, 0),
                new ExportTileCoordinate(2, 3, 1),
            ],
            options => options.WithStrictOrdering());
    }
}
