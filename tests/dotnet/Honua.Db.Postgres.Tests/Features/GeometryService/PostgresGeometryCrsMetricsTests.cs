// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.GeometryService;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.GeometryService;

/// <summary>
/// Proves geometry-service area routing follows WKT2 geocentric versus ellipsoidal
/// classification. A misclassified Cartesian GEODCRS takes the geography branch and
/// returns a spheroidal area many orders of magnitude above the planar unit square.
/// </summary>
[Collection("Database")]
public sealed class PostgresGeometryCrsMetricsTests : IAsyncLifetime
{
    private const int CartesianSrid = 998996;
    private const int EllipsoidalSrid = 998995;
    private const int GeoccsSrid = 998994;

    private readonly PostgresFixture _fixture = new();
    private PostgresGeometryOperationService? _service;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var connectionProvider = new PostgresDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        _service = new PostgresGeometryOperationService(connectionProvider);
    }

    public async Task DisposeAsync()
    {
        await RemoveAsync(CartesianSrid);
        await RemoveAsync(EllipsoidalSrid);
        await RemoveAsync(GeoccsSrid);
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task AreaAsync_CartesianGeodcrs_UsesPlanarMeters()
    {
        await InsertAsync(
            CartesianSrid,
            "GEODCRS[\"Test Geocentric\",DATUM[\"World Geodetic System 1984\",ELLIPSOID[\"WGS 84\",6378137,298.257223563]]," +
            "CS[Cartesian,3],AXIS[\"X\",geocentricX],AXIS[\"Y\",geocentricY],AXIS[\"Z\",geocentricZ],UNIT[\"metre\",1]]",
            "+proj=geocent +datum=WGS84 +units=m +no_defs");

        var area = await _service!.AreaAsync(UnitSquareWkb(), CartesianSrid);

        area.Should().BeApproximately(1d, 1e-9);
    }

    [Fact]
    public async Task AreaAsync_Wkt1Geoccs_UsesPlanarMeters()
    {
        await InsertAsync(
            GeoccsSrid,
            "GEOCCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"metre\",1]]",
            "+units=m +no_defs");

        var area = await _service!.AreaAsync(UnitSquareWkb(), GeoccsSrid);

        area.Should().BeApproximately(1d, 1e-9);
    }

    [Fact]
    public async Task AreaAsync_EllipsoidalGeodcrs_UsesGeography()
    {
        await InsertAsync(
            EllipsoidalSrid,
            "GEODCRS[\"Test Geographic\",DATUM[\"World Geodetic System 1984\",ELLIPSOID[\"WGS 84\",6378137,298.257223563]]," +
            "CS[ellipsoidal,2],AXIS[\"longitude\",east],AXIS[\"latitude\",north],UNIT[\"degree\",0.0174532925199433]]",
            "+proj=longlat +datum=WGS84 +no_defs");

        var area = await _service!.AreaAsync(UnitSquareWkb(), EllipsoidalSrid);

        // A 1°×1° patch at the origin is about 1.2e10 m² on the spheroid.
        // The planar branch would return 1, or the degree-unit factor squared.
        area.Should().BeGreaterThan(1e9);
    }

    private async Task InsertAsync(int srid, string srtext, string proj4text)
    {
        await _fixture.ApplyGlobalSeedSqlAsync(
            """
            INSERT INTO spatial_ref_sys (srid, auth_name, auth_srid, srtext, proj4text)
            VALUES (@srid, 'EPSG', @srid, @srtext, @proj4text)
            ON CONFLICT (srid) DO UPDATE
            SET auth_name = EXCLUDED.auth_name,
                auth_srid = EXCLUDED.auth_srid,
                srtext = EXCLUDED.srtext,
                proj4text = EXCLUDED.proj4text
            """,
            command =>
            {
                command.Parameters.AddWithValue("srid", srid);
                command.Parameters.AddWithValue("srtext", srtext);
                command.Parameters.AddWithValue("proj4text", proj4text);
            });
    }

    private async Task RemoveAsync(int srid)
    {
        await _fixture.ApplyGlobalSeedSqlAsync(
            "DELETE FROM spatial_ref_sys WHERE srid = @srid",
            command => command.Parameters.AddWithValue("srid", srid));
    }

    private static byte[] UnitSquareWkb()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(3);
        writer.Write(1);
        writer.Write(5);
        WriteCoord(writer, 0d, 0d);
        WriteCoord(writer, 1d, 0d);
        WriteCoord(writer, 1d, 1d);
        WriteCoord(writer, 0d, 1d);
        WriteCoord(writer, 0d, 0d);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteCoord(BinaryWriter writer, double x, double y)
    {
        writer.Write(x);
        writer.Write(y);
    }
}
