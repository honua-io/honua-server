// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Protocols.Ogc.Api.Tiles;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

public sealed class RasterQueryProjectionTests
{
    private static readonly TileBounds WebMercatorBounds =
        new(-1_000_000, -1_000_000, 1_000_000, 1_000_000);

    [Fact]
    public void CreateRasterQueryProjection_CustomStorageSrid_DelegatesTransformToProvider()
    {
        var projection = TilesEndpoints.CreateRasterQueryProjection(
            WebMercatorBounds,
            filterSrid: 3857,
            sourceSrid: 26915,
            usesRoutedReader: true);

        projection.QueryBounds.Should().Be(WebMercatorBounds);
        projection.QuerySrid.Should().Be(3857);
        projection.OutputSrid.Should().Be(3857);
        projection.RenderSourceSrid.Should().BeNull();
        projection.RenderTargetSrid.Should().BeNull();
        projection.UsesProviderTransform.Should().BeTrue();
    }

    [Fact]
    public void CreateRasterQueryProjection_WellKnownStorageSrid_UsesInMemoryTransform()
    {
        var projection = TilesEndpoints.CreateRasterQueryProjection(
            WebMercatorBounds,
            filterSrid: 3857,
            sourceSrid: 4326,
            usesRoutedReader: true);

        projection.QueryBounds.Should().NotBe(WebMercatorBounds);
        projection.QuerySrid.Should().Be(4326);
        projection.OutputSrid.Should().Be(4326);
        projection.RenderSourceSrid.Should().Be(4326);
        projection.RenderTargetSrid.Should().Be(3857);
        projection.UsesProviderTransform.Should().BeFalse();
    }

    [Fact]
    public void CreateRasterQueryProjection_UnroutedReader_PreservesTileMatrixCoordinates()
    {
        var projection = TilesEndpoints.CreateRasterQueryProjection(
            WebMercatorBounds,
            filterSrid: 3857,
            sourceSrid: 26915,
            usesRoutedReader: false);

        projection.QueryBounds.Should().Be(WebMercatorBounds);
        projection.QuerySrid.Should().Be(3857);
        projection.OutputSrid.Should().Be(3857);
        projection.RenderSourceSrid.Should().BeNull();
        projection.RenderTargetSrid.Should().BeNull();
        projection.UsesProviderTransform.Should().BeFalse();
    }
}
