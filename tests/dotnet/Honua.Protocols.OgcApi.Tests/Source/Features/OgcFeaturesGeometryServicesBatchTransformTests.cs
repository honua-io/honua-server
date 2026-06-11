// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Covers the batched coordinate transform on the OGC Features mutation path (#1593):
/// the coordinate-sequence transform issues one <c>TransformPointsAsync</c> call per
/// sequence instead of one awaited <c>TransformPointAsync</c> per vertex, and the
/// batched results are identical to the per-point default path.
/// </summary>
public sealed class OgcFeaturesGeometryServicesBatchTransformTests
{
    private const int InputSrid = 4326;
    private const int StorageSrid = 2193;

    [UnitTest]
    public async Task TransformPointsAsync_DefaultImplementation_MatchesPerPointPath()
    {
        ICoordinateTransformService service = new AffinePerPointTransformService();
        var xs = new[] { 1.0, 2.5, -3.25 };
        var ys = new[] { 4.0, -5.5, 6.75 };
        var expected = new List<(double X, double Y)>();
        for (var index = 0; index < xs.Length; index++)
        {
            var transformed = await service.TransformPointAsync(xs[index], ys[index], InputSrid, StorageSrid);
            expected.Add(transformed!.Value);
        }

        var success = await service.TransformPointsAsync(xs, ys, InputSrid, StorageSrid);

        success.Should().BeTrue();
        xs.Should().Equal(expected.Select(static point => point.X));
        ys.Should().Equal(expected.Select(static point => point.Y));
    }

    [UnitTest]
    public async Task TransformPointsAsync_DefaultImplementation_MismatchedLengths_Throws()
    {
        ICoordinateTransformService service = new AffinePerPointTransformService();

        var act = async () => await service.TransformPointsAsync(new double[2], new double[3], InputSrid, StorageSrid);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [UnitTest]
    public async Task TransformPointsAsync_DefaultImplementation_PointFailure_ReturnsFalse()
    {
        ICoordinateTransformService service = new FailingTransformService();

        var success = await service.TransformPointsAsync([1.0, 2.0], [3.0, 4.0], InputSrid, StorageSrid);

        success.Should().BeFalse();
    }

    [UnitTest]
    public async Task TryCreateWkbFromGeoJsonAsync_BatchTransform_MatchesPerPointTransform()
    {
        var batchService = new BatchingAffineTransformService();
        var perPointService = new AffinePerPointTransformService();
        var perPointSut = CreateSut(perPointService);
        var batchSut = CreateSut(batchService);

        var perPointResult = await perPointSut.TryCreateWkbFromGeoJsonAsync(PolygonWithHole(), InputSrid, StorageSrid);
        var batchResult = await batchSut.TryCreateWkbFromGeoJsonAsync(PolygonWithHole(), InputSrid, StorageSrid);

        perPointResult.IsSuccess.Should().BeTrue();
        batchResult.IsSuccess.Should().BeTrue();
        batchResult.Wkb.Should().Equal(perPointResult.Wkb);
        // The per-point service runs through the interface's default loop.
        perPointService.PerPointCalls.Should().BeGreaterThan(0);
        // One batched call per coordinate sequence: exterior ring + interior ring.
        batchService.BatchCalls.Should().Be(2);
        batchService.PerPointCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task TryCreateWkbFromGeoJsonAsync_BatchTransformFails_ReturnsFailure()
    {
        var sut = CreateSut(new FailingTransformService());

        var result = await sut.TryCreateWkbFromGeoJsonAsync(PolygonWithHole(), InputSrid, StorageSrid);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"SRID {InputSrid} to SRID {StorageSrid}");
    }

    private static SimpleGeoJsonGeometry PolygonWithHole() => new()
    {
        Type = "Polygon",
        CoordinatesJson = "[[[0,0],[10,0],[10,10],[0,10],[0,0]],[[2,2],[3,2],[3,3],[2,3],[2,2]]]"
    };

    private static OgcFeaturesGeometryServices CreateSut(ICoordinateTransformService transformService)
        => new(
            new Honua.Infrastructure.Services.GeometryService(Options.Create(new LimitsOptions())),
            transformService,
            Options.Create(new LimitsOptions()),
            NullLogger<OgcFeaturesGeometryServices>.Instance);

    private static (double X, double Y) Affine(double x, double y)
        => ((x * 2.0) + 100.0, (y * 0.5) - 50.0);

    private sealed class AffinePerPointTransformService : ICoordinateTransformService
    {
        public int PerPointCalls { get; private set; }

        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
        {
            PerPointCalls++;
            return ValueTask.FromResult<(double X, double Y)?>(Affine(x, y));
        }
    }

    private sealed class BatchingAffineTransformService : ICoordinateTransformService
    {
        public int PerPointCalls { get; private set; }

        public int BatchCalls { get; private set; }

        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
        {
            PerPointCalls++;
            return ValueTask.FromResult<(double X, double Y)?>(Affine(x, y));
        }

        public ValueTask<bool> TransformPointsAsync(
            double[] xs, double[] ys,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            for (var index = 0; index < xs.Length; index++)
            {
                (xs[index], ys[index]) = Affine(xs[index], ys[index]);
            }

            return ValueTask.FromResult(true);
        }
    }

    private sealed class FailingTransformService : ICoordinateTransformService
    {
        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y,
            int fromSrid, int toSrid,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(double X, double Y)?>(null);
    }
}
