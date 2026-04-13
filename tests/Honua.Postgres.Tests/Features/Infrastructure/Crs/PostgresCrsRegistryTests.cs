// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Crs;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Infrastructure.Crs;

[Collection("Database")]
public sealed class PostgresCrsRegistryTests : IAsyncLifetime
{
    private const int TestSrid = 998999;
    private const int GeocentricTestSrid = 998998;
    private readonly PostgresFixture _fixture = new();
    private PostgresCrsRegistry? _registry;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var connectionProvider = new PostgresDatabaseConnectionProvider(
            _fixture.DataSource,
            NullLogger<PostgresDatabaseConnectionProvider>.Instance);
        _registry = new PostgresCrsRegistry(connectionProvider, NullLogger<PostgresCrsRegistry>.Instance);
    }

    public async Task DisposeAsync()
    {
        await RemoveTestCrsAsync();
        await RemoveGeocentricTestCrsAsync();
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task ResolveBySridAsync_WithEastNorthAxisOrder_ReturnsEastNorth()
    {
        await InsertTestCrsAsync();

        var definition = await _registry!.ResolveBySridAsync(TestSrid);

        definition.Should().NotBeNull();
        definition!.Value.Srid.Should().Be(TestSrid);
        definition.Value.AxisOrder.Should().Be(AxisOrder.EastNorth);
        definition.Value.IsGeographic.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveBySridAsync_WithGeocentricWkt_ReturnsProjectedDefinition()
    {
        await InsertGeocentricTestCrsAsync();

        var definition = await _registry!.ResolveBySridAsync(GeocentricTestSrid);

        definition.Should().NotBeNull();
        definition!.Value.Srid.Should().Be(GeocentricTestSrid);
        definition.Value.IsGeographic.Should().BeFalse();
        definition.Value.AxisOrder.Should().Be(AxisOrder.EastNorth);
    }

    private async Task InsertTestCrsAsync()
    {
        const string srtext =
            "GEOGCRS[\"Test CRS\",DATUM[\"World Geodetic System 1984\",ELLIPSOID[\"WGS 84\",6378137,298.257223563]]," +
            "PRIMEM[\"Greenwich\",0],CS[ellipsoidal,2],AXIS[\"Longitude\",east],AXIS[\"Latitude\",north]," +
            "UNIT[\"degree\",0.0174532925199433]]";

        await using var connection = await _fixture.GetConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO spatial_ref_sys (srid, auth_name, auth_srid, srtext, proj4text)
            VALUES (@srid, 'EPSG', @srid, @srtext, '+proj=longlat +datum=WGS84 +no_defs')
            ON CONFLICT (srid) DO UPDATE
            SET auth_name = EXCLUDED.auth_name,
                auth_srid = EXCLUDED.auth_srid,
                srtext = EXCLUDED.srtext,
                proj4text = EXCLUDED.proj4text
            """, connection);
        command.Parameters.AddWithValue("srid", TestSrid);
        command.Parameters.AddWithValue("srtext", srtext);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RemoveTestCrsAsync()
    {
        await using var connection = await _fixture.GetConnectionAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM spatial_ref_sys WHERE srid = @srid",
            connection);
        command.Parameters.AddWithValue("srid", TestSrid);
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertGeocentricTestCrsAsync()
    {
        const string srtext =
            "GEODCRS[\"Test Geocentric CRS\",DATUM[\"World Geodetic System 1984\",ELLIPSOID[\"WGS 84\",6378137,298.257223563]]," +
            "CS[Cartesian,3],AXIS[\"X\",geocentricX],AXIS[\"Y\",geocentricY],AXIS[\"Z\",geocentricZ],UNIT[\"metre\",1]]";

        await using var connection = await _fixture.GetConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO spatial_ref_sys (srid, auth_name, auth_srid, srtext, proj4text)
            VALUES (@srid, 'EPSG', @srid, @srtext, '+proj=geocent +datum=WGS84 +no_defs')
            ON CONFLICT (srid) DO UPDATE
            SET auth_name = EXCLUDED.auth_name,
                auth_srid = EXCLUDED.auth_srid,
                srtext = EXCLUDED.srtext,
                proj4text = EXCLUDED.proj4text
            """, connection);
        command.Parameters.AddWithValue("srid", GeocentricTestSrid);
        command.Parameters.AddWithValue("srtext", srtext);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RemoveGeocentricTestCrsAsync()
    {
        await using var connection = await _fixture.GetConnectionAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM spatial_ref_sys WHERE srid = @srid",
            connection);
        command.Parameters.AddWithValue("srid", GeocentricTestSrid);
        await command.ExecuteNonQueryAsync();
    }
}
