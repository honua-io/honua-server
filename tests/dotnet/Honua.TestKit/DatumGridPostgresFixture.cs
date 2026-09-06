// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Db.Postgres.Features.GeometryService;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.TestKit;

/// <summary>A dedicated PostGIS operation database with the pinned NOAA NADCON grid.</summary>
public sealed class DatumGridPostgresFixture : IAsyncDisposable
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _source;

    /// <summary>Starts an isolated operation database without changing the shared base-image fixture.</summary>
    public async Task InitializeAsync()
    {
        await using var resource = typeof(DatumGridPostgresFixture).Assembly.GetManifestResourceStream(
            "Honua.TestKit.TestData.Proj.us_noaa_conus.tif")
            ?? throw new InvalidOperationException("The pinned NADCON fixture is missing.");
        using var bytes = new MemoryStream();
        await resource.CopyToAsync(bytes);
        _container = new PostgreSqlBuilder().WithImage("postgis/postgis:18-3.6")
            .WithDatabase("datum_fixture").WithUsername("test").WithPassword("test")
            .WithResourceMapping(bytes.ToArray(), "/usr/share/proj/us_noaa_conus.tif")
            .Build();
        await _container.StartAsync();
        _source = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var command = _source.CreateCommand("CREATE EXTENSION IF NOT EXISTS postgis");
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Uses the real provider against the grid database for HTTP geometry operations.</summary>
    public WebAppFixture ConfigureGeometryService(WebAppFixture fixture)
    {
        var source = _source ?? throw new InvalidOperationException("Initialize the datum fixture first.");
        return fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IGeometryOperationService>();
            services.AddSingleton<IGeometryOperationService>(new PostgresGeometryOperationService(
                new PostgresDatabaseConnectionProvider(source, NullLogger<PostgresDatabaseConnectionProvider>.Instance)));
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
