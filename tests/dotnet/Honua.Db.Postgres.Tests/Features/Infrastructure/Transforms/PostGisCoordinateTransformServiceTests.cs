// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Transforms;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Infrastructure.Transforms;

/// <summary>
/// Integration tests for PostGIS-backed coordinate transform service.
/// </summary>
[Collection("Database")]
public sealed class PostGisCoordinateTransformServiceTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private PostGisCoordinateTransformService? _service;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var connectionProvider = new PostgresDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        _service = new PostGisCoordinateTransformService(
            connectionProvider,
            NullLogger<PostGisCoordinateTransformService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    // --- Identity / in-memory fast path tests ---

    [IntegrationTest]
    public async Task TransformPointAsync_SameSrid_ReturnsIdentity()
    {
        var result = await _service!.TransformPointAsync(-122.4, 37.7, 4326, 4326);

        result.Should().NotBeNull();
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        point.X.Should().Be(-122.4);
        point.Y.Should().Be(37.7);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_WebMercatorAlias_ReturnsIdentity()
    {
        var result = await _service!.TransformPointAsync(100000.0, 200000.0, 3857, 102100);

        result.Should().NotBeNull();
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        point.X.Should().Be(100000.0);
        point.Y.Should().Be(200000.0);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_4326To3857_MatchesInMemory()
    {
        var result = await _service!.TransformPointAsync(0.0, 51.5, 4326, 3857);

        result.Should().NotBeNull();
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        point.X.Should().BeApproximately(0.0, 1.0);
        point.Y.Should().BeApproximately(6_711_455.0, 10_000.0);
    }

    // --- PostGIS fallback datum transform tests ---

    [IntegrationTest]
    public async Task TransformPointAsync_Nad83ToWgs84_ReturnsNonZeroOffset()
    {
        // NAD83 (4269) -> WGS84 (4326) has a small but non-zero offset (~1-2m)
        var result = await _service!.TransformPointAsync(-122.4194, 37.7749, 4269, 4326);

        result.Should().NotBeNull();
        // The offset should be small but the transform should not be identity
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        point.X.Should().BeApproximately(-122.4194, 0.01);
        point.Y.Should().BeApproximately(37.7749, 0.01);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_Nad27ToWgs84_ReturnsSignificantOffset()
    {
        // NAD27 (4267) -> WGS84 (4326) has a significant offset
        var result = await _service!.TransformPointAsync(-122.4194, 37.7749, 4267, 4326);

        result.Should().NotBeNull();
        // NAD27 to WGS84 offset is typically 10-100m, so coordinates should be close but measurably different
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        point.X.Should().BeApproximately(-122.4194, 0.1);
        point.Y.Should().BeApproximately(37.7749, 0.1);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_Wgs84ToWebMercator_RoundTripMatchesInMemory()
    {
        var lon = -122.4194;
        var lat = 37.7749;

        var toMerc = await _service!.TransformPointAsync(lon, lat, 4326, 3857);
        toMerc.Should().NotBeNull();
        var mercPoint = toMerc is { } tm ? tm : throw new InvalidOperationException("Expected a Web Mercator transform result.");

        var backToGeo = await _service!.TransformPointAsync(mercPoint.X, mercPoint.Y, 3857, 4326);
        backToGeo.Should().NotBeNull();
        var geoPoint = backToGeo is { } bg ? bg : throw new InvalidOperationException("Expected a round-trip transform result.");

        geoPoint.X.Should().BeApproximately(lon, 0.0001);
        geoPoint.Y.Should().BeApproximately(lat, 0.0001);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_UnknownSrid_ReturnsNull()
    {
        // SRID 999999 should not exist in spatial_ref_sys
        var result = await _service!.TransformPointAsync(0, 0, 999999, 4326);

        result.Should().BeNull();
    }

    // --- Batch point transform tests (#1593) ---

    [IntegrationTest]
    public async Task TransformPointsAsync_SameSrid_ReturnsIdentity()
    {
        var xs = new[] { -122.4, 0.0, 151.2 };
        var ys = new[] { 37.7, 51.5, -33.9 };

        var success = await _service!.TransformPointsAsync(xs, ys, 4326, 4326);

        success.Should().BeTrue();
        xs.Should().Equal(-122.4, 0.0, 151.2);
        ys.Should().Equal(37.7, 51.5, -33.9);
    }

    [IntegrationTest]
    public async Task TransformPointsAsync_Wgs84ToWebMercator_MatchesPerPointPath()
    {
        var xs = new[] { -122.4194, 0.0, 151.2093 };
        var ys = new[] { 37.7749, 51.5, -33.8688 };
        var expected = new List<(double X, double Y)>();
        for (var index = 0; index < xs.Length; index++)
        {
            var point = await _service!.TransformPointAsync(xs[index], ys[index], 4326, 3857);
            point.Should().NotBeNull();
            expected.Add(point!.Value);
        }

        var success = await _service!.TransformPointsAsync(xs, ys, 4326, 3857);

        success.Should().BeTrue();
        for (var index = 0; index < xs.Length; index++)
        {
            xs[index].Should().BeApproximately(expected[index].X, 1e-6);
            ys[index].Should().BeApproximately(expected[index].Y, 1e-6);
        }
    }

    [IntegrationTest]
    public async Task TransformPointsAsync_Nad27ToWgs84_SingleRoundTripMatchesPerPointPath()
    {
        var xs = new[] { -122.4194, -118.2437, -73.9857 };
        var ys = new[] { 37.7749, 34.0522, 40.7484 };
        var expected = new List<(double X, double Y)>();
        for (var index = 0; index < xs.Length; index++)
        {
            var point = await _service!.TransformPointAsync(xs[index], ys[index], 4267, 4326);
            point.Should().NotBeNull();
            expected.Add(point!.Value);
        }

        var success = await _service!.TransformPointsAsync(xs, ys, 4267, 4326);

        success.Should().BeTrue();
        for (var index = 0; index < xs.Length; index++)
        {
            xs[index].Should().BeApproximately(expected[index].X, 1e-9);
            ys[index].Should().BeApproximately(expected[index].Y, 1e-9);
        }
    }

    [IntegrationTest]
    public async Task TransformPointsAsync_UnknownSrid_ReturnsFalse()
    {
        var success = await _service!.TransformPointsAsync([0.0], [0.0], 999999, 4326);

        success.Should().BeFalse();
    }

    [UnitTest]
    public async Task TransformPointsAsync_MismatchedLengths_Throws()
    {
        var act = async () => await _service!.TransformPointsAsync(new double[2], new double[3], 4326, 3857);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // --- Extent transform tests ---

    [IntegrationTest]
    public async Task TransformExtentAsync_Nad83ToWgs84_ReturnsValidExtent()
    {
        var result = await _service!.TransformExtentAsync(
            -123.0, 37.0, -122.0, 38.0,
            4269, 4326);

        result.Should().NotBeNull();
        var extent = result is { } r ? r : throw new InvalidOperationException("Expected a transformed extent.");
        extent.MinX.Should().BeApproximately(-123.0, 0.01);
        extent.MinY.Should().BeApproximately(37.0, 0.01);
        extent.MaxX.Should().BeApproximately(-122.0, 0.01);
        extent.MaxY.Should().BeApproximately(38.0, 0.01);
    }

    [IntegrationTest]
    public async Task TransformExtentAsync_ConusAlbersToWgs84_TracksDensifiedBoundaryExtent()
    {
        const double minX = -2_500_000d;
        const double minY = 1_000_000d;
        const double maxX = 2_500_000d;
        const double maxY = 3_500_000d;
        const int fromSrid = 5070;
        const int toSrid = 4326;

        var result = await _service!.TransformExtentAsync(minX, minY, maxX, maxY, fromSrid, toSrid);
        var reference = await GetDensifiedReferenceExtentAsync(minX, minY, maxX, maxY, fromSrid, toSrid);

        result.Should().NotBeNull();
        var extent = result is { } r ? r : throw new InvalidOperationException("Expected a transformed extent.");
        extent.MinX.Should().BeApproximately(reference.MinX, 0.05);
        extent.MinY.Should().BeApproximately(reference.MinY, 0.05);
        extent.MaxX.Should().BeApproximately(reference.MaxX, 0.05);
        extent.MaxY.Should().BeApproximately(reference.MaxY, 0.05);
    }

    [IntegrationTest]
    public async Task TransformExtentAsync_AntimeridianCrossing_Wgs84ToWebMercator_ReturnsWrappedBounds()
    {
        // #2739: a dateline-crossing input (minX > maxX) must stay wrapped in the output, taking
        // its X bounds from the transformed western/eastern edges rather than collapsing the
        // sampled longitudes into a single inflated [-max,+max] span. The western edge (170) is
        // the output MinX and the eastern edge (-170) is the output MaxX, so MinX > MaxX.
        const double minX = 170.0;
        const double minY = -10.0;
        const double maxX = -170.0;
        const double maxY = 10.0;
        const int fromSrid = 4326;
        const int toSrid = 3857;

        var result = await _service!.TransformExtentAsync(
            minX, minY, maxX, maxY,
            fromSrid, toSrid);

        result.Should().NotBeNull();
        var projectedWestEdge = ProjectLonLatToWebMercator(-170.0, 0.0).X;
        var projectedEastEdge = ProjectLonLatToWebMercator(170.0, 0.0).X;
        var projectedSouth = ProjectLonLatToWebMercator(0.0, minY).Y;
        var projectedNorth = ProjectLonLatToWebMercator(0.0, maxY).Y;

        var extent = result is { } r ? r : throw new InvalidOperationException("Expected a transformed extent.");
        extent.MinX.Should().BeApproximately(projectedEastEdge, 1.0);
        extent.MaxX.Should().BeApproximately(projectedWestEdge, 1.0);
        extent.MinX.Should().BeGreaterThan(extent.MaxX);
        extent.MinY.Should().BeApproximately(projectedSouth, 1.0);
        extent.MaxY.Should().BeApproximately(projectedNorth, 1.0);
    }

    [IntegrationTest]
    public async Task TransformExtentAsync_SameSrid_ReturnsIdentity()
    {
        var result = await _service!.TransformExtentAsync(
            -180, -90, 180, 90,
            4326, 4326);

        result.Should().NotBeNull();
        var extent = result is { } r ? r : throw new InvalidOperationException("Expected a transformed extent.");
        extent.MinX.Should().Be(-180);
        extent.MinY.Should().Be(-90);
        extent.MaxX.Should().Be(180);
        extent.MaxY.Should().Be(90);
    }

    [UnitTest]
    public void EnumerateSampledExtentPoints_AntimeridianCrossing_StaysNearDateline()
    {
        // The extent sampling used by the in-memory transform path lives in the shared
        // WebMercatorMath helper; sampling a dateline-crossing extent interpolates longitude
        // across the antimeridian (170 -> 180 -> -170) rather than back through 0.
        var points = WebMercatorMath.EnumerateSampledExtentPoints(170.0, -10.0, -170.0, 10.0, 4).ToArray();

        points.Should().NotBeEmpty();
        points.Select(point => point.X).Should().NotContain(value => Math.Abs(value) < 1e-9);
        points.Select(point => point.X).Should().Contain(value => value >= 179.999 && value <= 180.001);
        points.Select(point => point.X).Should().Contain(value => value < -170.0);
    }

    // --- Antimeridian extent tests ---

    [IntegrationTest]
    public async Task TransformExtentAsync_AntimeridianCrossing_ProducesFiniteValues()
    {
        // Transform corners of an antimeridian-crossing extent individually
        var minResult = await _service!.TransformPointAsync(170.0, -10.0, 4326, 3857);
        var maxResult = await _service!.TransformPointAsync(-170.0, 10.0, 4326, 3857);

        minResult.Should().NotBeNull();
        maxResult.Should().NotBeNull();
        var min = minResult is { } mn ? mn : throw new InvalidOperationException("Expected a min-corner transform result.");
        var max = maxResult is { } mx ? mx : throw new InvalidOperationException("Expected a max-corner transform result.");
        double.IsFinite(min.X).Should().BeTrue();
        double.IsFinite(min.Y).Should().BeTrue();
        double.IsFinite(max.X).Should().BeTrue();
        double.IsFinite(max.Y).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TransformPointAsync_AtLon180_ProducesFiniteResult()
    {
        var result = await _service!.TransformPointAsync(180.0, 0.0, 4326, 3857);

        result.Should().NotBeNull();
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        double.IsFinite(point.X).Should().BeTrue();
        double.IsFinite(point.Y).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TransformPointAsync_AtLonNeg180_ProducesFiniteResult()
    {
        var result = await _service!.TransformPointAsync(-180.0, 0.0, 4326, 3857);

        result.Should().NotBeNull();
        var point = result is { } r ? r : throw new InvalidOperationException("Expected a transform result.");
        double.IsFinite(point.X).Should().BeTrue();
        double.IsFinite(point.Y).Should().BeTrue();
    }

    private async Task<(double MinX, double MinY, double MaxX, double MaxY)> GetDensifiedReferenceExtentAsync(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        int toSrid)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH envelope AS (
                SELECT ST_SetSRID(ST_MakeEnvelope(@minX, @minY, @maxX, @maxY), @fromSrid) AS geom
            ),
            densified AS (
                SELECT ST_Segmentize(
                    ST_ExteriorRing(geom),
                    GREATEST(ABS(@maxX - @minX), ABS(@maxY - @minY)) / 32.0
                ) AS geom
                FROM envelope
            )
            SELECT MIN(ST_X(point_geom)) AS xmin,
                   MIN(ST_Y(point_geom)) AS ymin,
                   MAX(ST_X(point_geom)) AS xmax,
                   MAX(ST_Y(point_geom)) AS ymax
            FROM (
                SELECT (ST_DumpPoints(ST_Transform(geom, @toSrid))).geom AS point_geom
                FROM densified
            ) transformed
            """;

        AddParameter(command, "@minX", minX);
        AddParameter(command, "@minY", minY);
        AddParameter(command, "@maxX", maxX);
        AddParameter(command, "@maxY", maxY);
        AddParameter(command, "@fromSrid", fromSrid);
        AddParameter(command, "@toSrid", toSrid);

        await using var reader = await command.ExecuteReaderAsync();
        reader.Read().Should().BeTrue();

        return (
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static (double X, double Y) ProjectLonLatToWebMercator(double longitude, double latitude)
    {
        const double earthRadius = 6_378_137.0;
        const double maxLatitude = 85.05112877980659;

        var clampedLat = Math.Clamp(latitude, -maxLatitude, maxLatitude);
        var x = longitude * Math.PI / 180.0 * earthRadius;
        var y = Math.Log(Math.Tan((90.0 + clampedLat) * Math.PI / 360.0)) * earthRadius;
        return (x, y);
    }
}
