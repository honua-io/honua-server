// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Core.Tests.Features.Tiles.PMTiles;

public class HilbertCurveTests
{
    [Fact]
    public void XYZToTileId_Zoom0_Returns0()
    {
        HilbertCurve.XYZToTileId(0, 0, 0).Should().Be(0UL);
    }

    [Theory]
    [InlineData(1, 0, 0, 1)]
    [InlineData(1, 0, 1, 2)]
    [InlineData(1, 1, 1, 3)]
    [InlineData(1, 1, 0, 4)]
    public void XYZToTileId_Zoom1_ReturnsExpectedIds(int z, int x, int y, ulong expectedId)
    {
        HilbertCurve.XYZToTileId(z, x, y).Should().Be(expectedId);
    }

    [Fact]
    public void XYZToTileId_Zoom2_HasExpectedOffset()
    {
        // Zoom 2 starts at tile ID 5 (1 + 4 = 5)
        var id = HilbertCurve.XYZToTileId(2, 0, 0);
        id.Should().BeGreaterOrEqualTo(5UL);
        id.Should().BeLessThan(5UL + 16UL); // 4^2 = 16 tiles at zoom 2
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 3, 3)]
    [InlineData(3, 7, 7)]
    [InlineData(5, 15, 20)]
    [InlineData(10, 512, 512)]
    public void RoundTrip_TileIdToXYZ_ReturnsOriginal(int z, int x, int y)
    {
        var tileId = HilbertCurve.XYZToTileId(z, x, y);
        var (rz, rx, ry) = HilbertCurve.TileIdToXYZ(tileId);
        rz.Should().Be(z);
        rx.Should().Be(x);
        ry.Should().Be(y);
    }

    [Fact]
    public void XYZToTileId_DifferentCoordinates_ProduceDifferentIds()
    {
        var ids = new HashSet<ulong>();
        for (var z = 0; z <= 3; z++)
        {
            var n = 1 << z;
            for (var x = 0; x < n; x++)
            {
                for (var y = 0; y < n; y++)
                {
                    var id = HilbertCurve.XYZToTileId(z, x, y);
                    ids.Add(id).Should().BeTrue($"tile ({z}/{x}/{y}) produced duplicate id {id}");
                }
            }
        }
    }

    [Fact]
    public void XYZToTileId_IdsAreContiguous()
    {
        // All tile IDs from zoom 0 through 3 should form a contiguous range 0..N
        var ids = new List<ulong>();
        for (var z = 0; z <= 3; z++)
        {
            var n = 1 << z;
            for (var x = 0; x < n; x++)
            {
                for (var y = 0; y < n; y++)
                {
                    ids.Add(HilbertCurve.XYZToTileId(z, x, y));
                }
            }
        }

        ids.Sort();

        // Total tiles: 1 + 4 + 16 + 64 = 85
        ids.Should().HaveCount(85);
        for (var i = 0; i < ids.Count; i++)
        {
            ids[i].Should().Be((ulong)i, $"tile at index {i} should have id {i}");
        }
    }
}
