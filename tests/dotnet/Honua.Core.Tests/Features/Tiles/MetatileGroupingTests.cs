// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Tests.Features.Tiles;

public class MetatileGroupingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public void NormalizeFactor_ClampsBelowOneToOne(int input, int expected)
    {
        MetatileGrouping.NormalizeFactor(input).Should().Be(expected);
    }

    [Fact]
    public void Group_FactorOne_YieldsOneTilePerBlock()
    {
        var tiles = new[]
        {
            new TileIndex(2, 0, 0),
            new TileIndex(2, 1, 0),
            new TileIndex(2, 0, 1)
        };

        var blocks = MetatileGrouping.Group(tiles, factor: 1);

        blocks.Should().HaveCount(3);
        blocks.Should().OnlyContain(block => block.Tiles.Count == 1);
    }

    [Fact]
    public void Group_FactorTwo_GroupsAlignedBlock()
    {
        // A full 2x2 block at z=3 starting at origin (0,0).
        var tiles = new[]
        {
            new TileIndex(3, 0, 0),
            new TileIndex(3, 1, 0),
            new TileIndex(3, 0, 1),
            new TileIndex(3, 1, 1)
        };

        var blocks = MetatileGrouping.Group(tiles, factor: 2);

        blocks.Should().HaveCount(1);
        var block = blocks[0];
        block.Z.Should().Be(3);
        block.MinX.Should().Be(0);
        block.MinY.Should().Be(0);
        block.Tiles.Should().HaveCount(4);
    }

    [Fact]
    public void Group_FactorTwo_AlignsToBlockGridAcrossBoundary()
    {
        // x=2 belongs to block origin 2 (not 0) with factor 2, so this splits into two blocks.
        var tiles = new[]
        {
            new TileIndex(3, 1, 0),
            new TileIndex(3, 2, 0)
        };

        var blocks = MetatileGrouping.Group(tiles, factor: 2);

        blocks.Should().HaveCount(2);
        blocks.Should().Contain(block => block.MinX == 0);
        blocks.Should().Contain(block => block.MinX == 2);
    }

    [Fact]
    public void Group_SeparatesByZoomLevel()
    {
        var tiles = new[]
        {
            new TileIndex(1, 0, 0),
            new TileIndex(2, 0, 0)
        };

        var blocks = MetatileGrouping.Group(tiles, factor: 4);

        blocks.Should().HaveCount(2);
        blocks.Select(block => block.Z).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void Group_PreservesEveryRequestedTileExactlyOnce()
    {
        var tiles = new[]
        {
            new TileIndex(4, 0, 0),
            new TileIndex(4, 1, 1),
            new TileIndex(4, 5, 7),
            new TileIndex(4, 6, 6)
        };

        var blocks = MetatileGrouping.Group(tiles, factor: 4);

        var flattened = blocks.SelectMany(block => block.Tiles).ToArray();
        flattened.Should().BeEquivalentTo(tiles);
    }

    [Fact]
    public void Group_IsDeterministicAcrossOrdering()
    {
        var ordered = new[]
        {
            new TileIndex(4, 0, 0),
            new TileIndex(4, 1, 0),
            new TileIndex(4, 0, 1),
            new TileIndex(4, 1, 1)
        };
        var shuffled = new[]
        {
            new TileIndex(4, 1, 1),
            new TileIndex(4, 0, 0),
            new TileIndex(4, 1, 0),
            new TileIndex(4, 0, 1)
        };

        var fromOrdered = MetatileGrouping.Group(ordered, factor: 2);
        var fromShuffled = MetatileGrouping.Group(shuffled, factor: 2);

        fromOrdered.Should().BeEquivalentTo(fromShuffled, options => options.WithStrictOrdering());
    }
}
