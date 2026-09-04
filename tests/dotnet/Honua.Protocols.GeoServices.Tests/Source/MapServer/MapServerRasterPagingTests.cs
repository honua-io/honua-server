// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

public sealed class MapServerRasterPagingTests
{
    [UnitTest]
    public async Task QueryAllRasterFeaturePagesAsync_MoreThanConfiguredPageSize_ReadsEveryFeatureInStableOrder()
    {
        const int pageSize = 10_000;
        var feature = Feature.Create(1, geometry: null, ImmutableDictionary<string, object?>.Empty);
        var firstPage = ImmutableArray.CreateRange(Enumerable.Repeat(feature, pageSize));
        var finalPage = ImmutableArray.Create(feature);
        var reader = Substitute.For<IFeatureReader, IPagedFeatureReader>();
        var pagedReader = (IPagedFeatureReader)reader;
        pagedReader.QueryPageAsync(17, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<FeatureQuery>(1).Offset switch
            {
                null or 0 => PagedQueryResult<Feature>.Create(firstPage, hasMoreResults: true),
                pageSize => PagedQueryResult<Feature>.Create(finalPage),
                _ => PagedQueryResult<Feature>.Empty()
            });
        var query = new FeatureQuery
        {
            Limit = pageSize,
            OrderBy = [new OrderByClause("objectid")]
        };

        var total = 0;
        await foreach (var page in RasterMapRenderingPipeline.QueryAllRasterFeaturePagesAsync(
                           reader,
                           layerId: 17,
                           query,
                           CancellationToken.None))
        {
            total += page.Length;
        }

        total.Should().Be(10_001);
        await pagedReader.Received(1).QueryPageAsync(
            17,
            Arg.Is<FeatureQuery>(candidate =>
                candidate.Offset == pageSize &&
                candidate.OrderBy.HasValue &&
                candidate.OrderBy.Value[0].Field == "objectid"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task QueryAllRasterFeaturePagesAsync_NonPagedReader_KeepsEachReadBounded()
    {
        const int pageSize = 2;
        var feature = Feature.Create(1, geometry: null, ImmutableDictionary<string, object?>.Empty);
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(17, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<FeatureQuery>(1).Offset switch
            {
                null or 0 => QueryResult<Feature>.Create(3, ImmutableArray.Create(feature, feature), hasMoreResults: true),
                pageSize => QueryResult<Feature>.Create(3, ImmutableArray.Create(feature)),
                _ => QueryResult<Feature>.Empty()
            });
        var query = new FeatureQuery
        {
            Limit = pageSize,
            OrderBy = [new OrderByClause("objectid")]
        };

        var total = 0;
        await foreach (var page in RasterMapRenderingPipeline.QueryAllRasterFeaturePagesAsync(
                           reader,
                           layerId: 17,
                           query,
                           CancellationToken.None))
        {
            total += page.Length;
        }

        total.Should().Be(3);
        await reader.Received(1).QueryAsync(
            17,
            Arg.Is<FeatureQuery>(candidate =>
                candidate.Offset == pageSize &&
                candidate.Limit == pageSize &&
                candidate.OrderBy.HasValue &&
                candidate.OrderBy.Value[0].Field == "objectid"),
            Arg.Any<CancellationToken>());
        await reader.DidNotReceive().QueryAsync(
            17,
            Arg.Is<FeatureQuery>(candidate => candidate.Limit == null),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryRenderAllRasterPointPagesAsync_FullFirstPage_ReadsAndRendersFollowingPage()
    {
        var reader = Substitute.For<IFeatureReader, IRasterPointReader>();
        var pointReader = (IRasterPointReader)reader;
        pointReader.QueryProjectedPointsAsync(17, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<FeatureQuery>(1).Offset switch
            {
                null or 0 => ImmutableArray.Create(new ProjectedPoint(1, 1), new ProjectedPoint(2, 2)),
                2 => ImmutableArray.Create(new ProjectedPoint(3, 3)),
                _ => ImmutableArray<ProjectedPoint>.Empty
            });
        var stylePlan = RasterMapRenderingPipeline.BuildRasterStylePlanFromJson(
            """[{"id":"points","type":"circle","paint":{"circle-radius":2,"circle-color":"#f00"}}]""");
        var query = new FeatureQuery
        {
            Limit = 2,
            ExcludeAttributes = true,
            SpatialFilter = SpatialFilter.Create([1], SpatialRelationship.Intersects, 4326),
            OrderBy = [new OrderByClause("objectid")]
        };
        using var surface = SKSurface.Create(new SKImageInfo(16, 16));

        var total = await RasterMapRenderingPipeline.TryRenderAllRasterPointPagesAsync(
            surface.Canvas,
            reader,
            layerId: 17,
            MetadataV2GeometryType.Point,
            stylePlan,
            query,
            new SkiaMapRenderer.RenderExtent(0, 0, 16, 16),
            imageWidth: 16,
            imageHeight: 16,
            static (x, y) => new SKPoint((float)x, (float)y),
            CancellationToken.None);

        total.Should().Be(3);
        await pointReader.Received(1).QueryProjectedPointsAsync(
            17,
            Arg.Is<FeatureQuery>(candidate =>
                candidate.Offset == 2 &&
                candidate.Limit == 2 &&
                candidate.RasterPointGrid != null &&
                candidate.OrderBy.HasValue &&
                candidate.OrderBy.Value[0].Field == "objectid"),
            Arg.Any<CancellationToken>());
    }
}
