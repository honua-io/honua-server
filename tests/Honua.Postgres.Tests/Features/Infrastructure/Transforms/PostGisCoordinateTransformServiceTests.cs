// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
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
        result!.Value.X.Should().Be(-122.4);
        result.Value.Y.Should().Be(37.7);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_WebMercatorAlias_ReturnsIdentity()
    {
        var result = await _service!.TransformPointAsync(100000.0, 200000.0, 3857, 102100);

        result.Should().NotBeNull();
        result!.Value.X.Should().Be(100000.0);
        result.Value.Y.Should().Be(200000.0);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_4326To3857_MatchesInMemory()
    {
        var result = await _service!.TransformPointAsync(0.0, 51.5, 4326, 3857);

        result.Should().NotBeNull();
        result!.Value.X.Should().BeApproximately(0.0, 1.0);
        result.Value.Y.Should().BeApproximately(6_711_455.0, 10_000.0);
    }

    // --- PostGIS fallback datum transform tests ---

    [IntegrationTest]
    public async Task TransformPointAsync_Nad83ToWgs84_ReturnsNonZeroOffset()
    {
        // NAD83 (4269) -> WGS84 (4326) has a small but non-zero offset (~1-2m)
        var result = await _service!.TransformPointAsync(-122.4194, 37.7749, 4269, 4326);

        result.Should().NotBeNull();
        // The offset should be small but the transform should not be identity
        result!.Value.X.Should().BeApproximately(-122.4194, 0.01);
        result.Value.Y.Should().BeApproximately(37.7749, 0.01);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_Nad27ToWgs84_ReturnsSignificantOffset()
    {
        // NAD27 (4267) -> WGS84 (4326) has a significant offset
        var result = await _service!.TransformPointAsync(-122.4194, 37.7749, 4267, 4326);

        result.Should().NotBeNull();
        // NAD27 to WGS84 offset is typically 10-100m, so coordinates should be close but measurably different
        result!.Value.X.Should().BeApproximately(-122.4194, 0.1);
        result.Value.Y.Should().BeApproximately(37.7749, 0.1);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_Wgs84ToWebMercator_RoundTripMatchesInMemory()
    {
        var lon = -122.4194;
        var lat = 37.7749;

        var toMerc = await _service!.TransformPointAsync(lon, lat, 4326, 3857);
        toMerc.Should().NotBeNull();

        var backToGeo = await _service!.TransformPointAsync(toMerc!.Value.X, toMerc.Value.Y, 3857, 4326);
        backToGeo.Should().NotBeNull();

        backToGeo!.Value.X.Should().BeApproximately(lon, 0.0001);
        backToGeo.Value.Y.Should().BeApproximately(lat, 0.0001);
    }

    [IntegrationTest]
    public async Task TransformPointAsync_UnknownSrid_ReturnsNull()
    {
        // SRID 999999 should not exist in spatial_ref_sys
        var result = await _service!.TransformPointAsync(0, 0, 999999, 4326);

        result.Should().BeNull();
    }

    // --- Extent transform tests ---

    [IntegrationTest]
    public async Task TransformExtentAsync_Nad83ToWgs84_ReturnsValidExtent()
    {
        var result = await _service!.TransformExtentAsync(
            -123.0, 37.0, -122.0, 38.0,
            4269, 4326);

        result.Should().NotBeNull();
        result!.Value.MinX.Should().BeApproximately(-123.0, 0.01);
        result.Value.MinY.Should().BeApproximately(37.0, 0.01);
        result.Value.MaxX.Should().BeApproximately(-122.0, 0.01);
        result.Value.MaxY.Should().BeApproximately(38.0, 0.01);
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
        result!.Value.MinX.Should().BeApproximately(reference.MinX, 0.05);
        result.Value.MinY.Should().BeApproximately(reference.MinY, 0.05);
        result.Value.MaxX.Should().BeApproximately(reference.MaxX, 0.05);
        result.Value.MaxY.Should().BeApproximately(reference.MaxY, 0.05);
    }

    [IntegrationTest]
    public async Task TransformExtentAsync_SameSrid_ReturnsIdentity()
    {
        var result = await _service!.TransformExtentAsync(
            -180, -90, 180, 90,
            4326, 4326);

        result.Should().NotBeNull();
        result!.Value.MinX.Should().Be(-180);
        result.Value.MinY.Should().Be(-90);
        result.Value.MaxX.Should().Be(180);
        result.Value.MaxY.Should().Be(90);
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
        double.IsFinite(minResult!.Value.X).Should().BeTrue();
        double.IsFinite(minResult.Value.Y).Should().BeTrue();
        double.IsFinite(maxResult!.Value.X).Should().BeTrue();
        double.IsFinite(maxResult.Value.Y).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TransformPointAsync_AtLon180_ProducesFiniteResult()
    {
        var result = await _service!.TransformPointAsync(180.0, 0.0, 4326, 3857);

        result.Should().NotBeNull();
        double.IsFinite(result!.Value.X).Should().BeTrue();
        double.IsFinite(result.Value.Y).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TransformPointAsync_AtLonNeg180_ProducesFiniteResult()
    {
        var result = await _service!.TransformPointAsync(-180.0, 0.0, 4326, 3857);

        result.Should().NotBeNull();
        double.IsFinite(result!.Value.X).Should().BeTrue();
        double.IsFinite(result.Value.Y).Should().BeTrue();
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
}
