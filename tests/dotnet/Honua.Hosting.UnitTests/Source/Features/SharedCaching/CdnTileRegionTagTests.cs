// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Microsoft.AspNetCore.Http;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class CdnTileRegionTagTests
{
    [UnitTheory]
    [InlineData("/tiles/3/tile.json")]
    [InlineData("/TILES/03/TILE.JSON")]
    public void ForNativeTile_MetadataGetsItsOwnRegionalPurgeTag(string path)
    {
        CdnTileRegionTags.ForNativeTile(new PathString(path), new TileOptions())
            .Should().Equal(CdnTileRegionTags.Metadata(3));
    }

    [UnitTest]
    public void Invalidations_LocalEdit_ReachesIntersectingTilesAtEveryZoom()
    {
        var bounds = BoundingBox.FromArray([-112d, 33d, -111d, 34d], 4326);
        var purges = CdnTileRegionTags.Invalidations(3, bounds);

        for (var zoom = 0; zoom <= 20; zoom++)
        {
            var size = 1 << zoom;
            var column = (int)Math.Floor(68.5d / 360d * size);
            var row = (int)Math.Floor((1d - Math.Asinh(Math.Tan(33.5d * Math.PI / 180d)) / Math.PI) / 2d * size);
            CdnTileRegionTags.ForTile(3, new(zoom, column, row))
                .Should().Contain(tag => purges.Contains("#" + tag));
        }
    }

    [UnitTest]
    public void Invalidations_LocalEdit_PreservesDistantDetailedTilesAndOtherLayers()
    {
        var purges = CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([-112d, 33d, -111d, 34d], 4326));

        CdnTileRegionTags.ForTile(3, new(10, 900, 900)).Should().NotContain(tag => purges.Contains("#" + tag));
        CdnTileRegionTags.ForTile(4, new(0, 0, 0)).Should().NotContain(tag => purges.Contains("#" + tag));
    }

    [UnitTest]
    public void Invalidations_BoundaryPoint_ReachesBothSidesOfTheEquatorAndMeridian()
    {
        var purges = CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([0d, 0d, 0d, 0d], 4326));

        foreach (var column in new[] { 7, 8 })
        {
            foreach (var row in new[] { 7, 8 })
            {
                CdnTileRegionTags.ForTile(3, new(4, column, row))
                    .Should().Contain(tag => purges.Contains("#" + tag));
            }
        }
    }

    [UnitTest]
    public void Invalidations_EditWithinTileBuffer_ReachesTheAdjacentTile()
    {
        var purges = CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([-0.1d, 1d, -0.1d, 1d], 4326));

        CdnTileRegionTags.ForTile(3, new(4, 8, 7), 256d / 4096d)
            .Should().Contain(tag => purges.Contains("#" + tag));
    }

    [UnitTheory]
    [InlineData(100d)]
    [InlineData(double.MaxValue)]
    public void ForTile_LargeBuffer_UsesBoundedFallbackReachedByEveryRegionalPurge(double buffer)
    {
        var tags = CdnTileRegionTags.ForTile(3, new(4, 8, 7), buffer);
        var purges = CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([170d, -80d, 171d, -79d], 4326));

        tags.Should().ContainSingle().Which.Should().Be("honua-buffered:tiles/3");
        tags.Should().Contain(tag => purges.Contains("#" + tag));
    }

    [UnitTest]
    public void Invalidations_WholeWorld_StaysWithinBudgetAndReachesEveryCoarseCell()
    {
        var purges = CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([-180d, -90d, 180d, 90d], 4326));

        purges.Should().HaveCountLessThanOrEqualTo(CdnTileRegionTags.MaxInvalidations);
        for (var column = 0; column < 16; column++)
        {
            for (var row = 0; row < 16; row++)
            {
                CdnTileRegionTags.ForTile(3, new(4, column, row))
                    .Should().Contain(tag => purges.Contains("#" + tag));
            }
        }
    }

    [UnitTest]
    public void ForNativeTile_ConfiguredBuffer_CoversTheRenderedBounds()
    {
        var options = new TileOptions { TileExtent = 1024, TileBuffer = 256 };
        var tags = CdnTileRegionTags.ForNativeTile(new PathString("/tiles/3/4/8/7.mvt"), options);
        var purges = CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([-4d, 1d, -4d, 1d], 4326));

        tags.Should().Contain(tag => purges.Contains("#" + tag));
    }

    [UnitTheory]
    [InlineData("/tiles/3/4/16/7.mvt")]
    [InlineData("/tiles/3/4/8/7.png")]
    [InlineData("/ogc/tiles/collections/3/tiles/WebMercatorQuad/4/8/7")]
    public void ForNativeTile_UnsupportedPath_DoesNotInventMercatorTags(string path)
        => CdnTileRegionTags.ForNativeTile(new PathString(path), new TileOptions()).Should().BeEmpty();

    [UnitTheory]
    [InlineData(-1, 0, 0)]
    [InlineData(31, 0, 0)]
    [InlineData(2, 4, 0)]
    [InlineData(2, 0, -1)]
    public void ForTile_InvalidAddress_RejectsAddress(int zoom, int column, int row)
    {
        Action act = () => CdnTileRegionTags.ForTile(3, new(zoom, column, row));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [UnitTest]
    public void Invalidations_ProjectedExtent_RejectsCoordinates()
    {
        Action act = () => CdnTileRegionTags.Invalidations(3, BoundingBox.FromArray([0d, 0d, 1d, 1d], 3857));
        act.Should().Throw<ArgumentException>();
    }
}
