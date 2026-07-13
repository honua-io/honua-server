// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Raster;

/// <summary>
/// Contract tests for the neutral raster-catalog query abstraction (#2666): validated spatial
/// predicate / CRS / paging input, and the reference in-memory evaluation that is the declared
/// bounded fallback and the oracle the PostGIS pushdown must match (envelope-intersects, identity,
/// temporal newest-batch, natural ordering, paging, count, aggregate extent).
/// </summary>
public sealed class RasterCatalogQueryTests
{
    [UnitTest]
    public void SpatialPredicate_ValidBox_IsAccepted()
    {
        RasterCatalogSpatialPredicate.TryCreate(0, 0, 10, 10, 4326, out var predicate, out var error)
            .Should().BeTrue();
        error.Should().BeEmpty();
        predicate.XMin.Should().Be(0);
        predicate.XMax.Should().Be(10);
        predicate.Srid.Should().Be(4326);
    }

    [UnitTest]
    public void SpatialPredicate_NullSrid_IsAccepted()
    {
        RasterCatalogSpatialPredicate.TryCreate(0, 0, 1, 1, srid: null, out var predicate, out _)
            .Should().BeTrue();
        predicate.Srid.Should().BeNull();
    }

    [UnitTest]
    public void SpatialPredicate_InvertedBox_IsRejected()
    {
        RasterCatalogSpatialPredicate.TryCreate(10, 0, 0, 10, 4326, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("inverted");
    }

    [UnitTest]
    public void SpatialPredicate_NonFiniteCoordinate_IsRejected()
    {
        RasterCatalogSpatialPredicate.TryCreate(0, 0, double.NaN, 10, 4326, out _, out var nanError)
            .Should().BeFalse();
        nanError.Should().Contain("finite");

        RasterCatalogSpatialPredicate.TryCreate(0, 0, double.PositiveInfinity, 10, 4326, out _, out _)
            .Should().BeFalse();
    }

    [UnitTest]
    public void SpatialPredicate_NonPositiveSrid_IsRejected()
    {
        RasterCatalogSpatialPredicate.TryCreate(0, 0, 1, 1, srid: 0, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("SRID");
    }

    [UnitTest]
    public void SpatialPredicate_Create_ThrowsOnInvalidInput()
    {
        var act = () => RasterCatalogSpatialPredicate.Create(10, 0, 0, 10, 4326);
        act.Should().Throw<RasterCatalogValidationException>();
    }

    [UnitTest]
    public void Query_Validate_RejectsNegativeOffset()
    {
        var query = new RasterCatalogQuery { Offset = -1 };
        var act = query.Validate;
        act.Should().Throw<RasterCatalogValidationException>();
    }

    [UnitTest]
    public void Query_Validate_RejectsNonPositiveLimit()
    {
        var query = new RasterCatalogQuery { Limit = 0 };
        var act = query.Validate;
        act.Should().Throw<RasterCatalogValidationException>();
    }

    [UnitTest]
    public async Task Evaluate_EnvelopeIntersects_IsEdgeInclusive()
    {
        var rasters = new[]
        {
            Raster(1, xMin: 0, yMin: 0, xMax: 1, yMax: 1),
            Raster(2, xMin: 5, yMin: 5, xMax: 6, yMax: 6),
        };

        // A filter box whose left edge exactly touches raster 1's right edge must still match it
        // (inclusive envelope-intersects), mirroring the PostGIS && bounding-box operator.
        var query = new RasterCatalogQuery
        {
            SpatialPredicate = RasterCatalogSpatialPredicate.Create(1, 0, 4, 1, 4326),
        };

        var page = await RasterCatalogQueryEvaluator.EvaluateAsync(rasters, query, null, default);

        page.Rasters.Select(r => r.Id).Should().Equal(1);
        page.PredicatePushedDown.Should().BeFalse();
    }

    [UnitTest]
    public async Task Evaluate_ObjectIds_FiltersToIdentitySet()
    {
        var rasters = new[] { Raster(1), Raster(2), Raster(3) };
        var query = new RasterCatalogQuery { ObjectIds = [1, 3] };

        var page = await RasterCatalogQueryEvaluator.EvaluateAsync(rasters, query, null, default);

        page.Rasters.Select(r => r.Id).Should().BeEquivalentTo([1L, 3L]);
    }

    [UnitTest]
    public async Task Evaluate_Timestamp_SelectsNewestBatchAtOrBeforeInstant()
    {
        var rasters = new[]
        {
            Raster(1, acquisition: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Raster(2, acquisition: new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            Raster(3, acquisition: new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        var query = new RasterCatalogQuery
        {
            Timestamp = new DateTimeOffset(2024, 2, 15, 0, 0, 0, TimeSpan.Zero),
        };

        var page = await RasterCatalogQueryEvaluator.EvaluateAsync(rasters, query, null, default);

        // Newest acquisition at or before 2024-02-15 is 2024-01-01 (raster 1 only).
        page.Rasters.Select(r => r.Id).Should().Equal(1);
    }

    [UnitTest]
    public async Task Evaluate_PagingPreservesInputOrderAndReportsTotal()
    {
        var rasters = new[] { Raster(10), Raster(20), Raster(30), Raster(40) };
        var query = new RasterCatalogQuery { Offset = 1, Limit = 2, IncludeTotalCount = true };

        var page = await RasterCatalogQueryEvaluator.EvaluateAsync(rasters, query, null, default);

        page.Rasters.Select(r => r.Id).Should().Equal(20, 30);
        page.TotalCount.Should().Be(4);
        page.RowsReturned.Should().Be(2);
        page.RowsScanned.Should().Be(4);
    }

    [UnitTest]
    public async Task Evaluate_AggregateExtent_UnionsMatchedSet()
    {
        var rasters = new[]
        {
            Raster(1, xMin: -10, yMin: -5, xMax: 0, yMax: 5),
            Raster(2, xMin: 0, yMin: -5, xMax: 10, yMax: 5),
        };
        var query = new RasterCatalogQuery { IncludeAggregateExtent = true };

        var page = await RasterCatalogQueryEvaluator.EvaluateAsync(rasters, query, null, default);

        page.AggregateExtent.Should().NotBeNull();
        page.AggregateExtent!.Value.XMin.Should().Be(-10);
        page.AggregateExtent!.Value.XMax.Should().Be(10);
    }

    [UnitTest]
    public async Task Evaluate_CrossSridPredicate_TransformsBeforeIntersect()
    {
        // Footprint is in 3857; the filter is in 4326 and the fake CRS pipeline maps it onto the
        // footprint. Without a transform service the SRID mismatch conservatively excludes the row.
        var rasters = new[] { Raster(1, xMin: 0, yMin: 0, xMax: 100, yMax: 100, srid: 3857) };
        var query = new RasterCatalogQuery
        {
            SpatialPredicate = RasterCatalogSpatialPredicate.Create(-1, -1, 1, 1, 4326),
        };

        var withTransform = await RasterCatalogQueryEvaluator.EvaluateAsync(
            rasters, query, new StubTransform(10, 10, 50, 50), default);
        withTransform.Rasters.Select(r => r.Id).Should().Equal(1);

        var withoutTransform = await RasterCatalogQueryEvaluator.EvaluateAsync(rasters, query, null, default);
        withoutTransform.Rasters.Should().BeEmpty();
    }

    private static RasterInfo Raster(
        long id,
        double xMin = -1,
        double yMin = -1,
        double xMax = 1,
        double yMax = 1,
        int srid = 4326,
        DateTimeOffset? acquisition = null) => new()
        {
            Id = id,
            LayerId = 1,
            Name = $"raster-{id}",
            Width = 256,
            Height = 256,
            BandCount = 1,
            PixelType = "8BUI",
            Srid = srid,
            Extent = new RasterExtent { XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax, Srid = srid },
            AcquisitionDate = acquisition,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

    private sealed class StubTransform(double minX, double minY, double maxX, double maxY)
        : ICoordinateTransformService
    {
        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minXIn, double minYIn, double maxXIn, double maxYIn,
            int fromSrid, int toSrid, CancellationToken cancellationToken = default)
            => new(((double, double, double, double)?)(minX, minY, maxX, maxY));

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
            => new(((double, double)?)(x, y));
    }
}
