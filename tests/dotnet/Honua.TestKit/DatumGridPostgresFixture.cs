// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DotNet.Testcontainers.Configurations;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Db.Postgres.Features.GeometryService;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.TestKit;

/// <summary>A dedicated PostGIS operation database with an optional pinned NOAA NADCON grid.</summary>
public sealed class DatumGridPostgresFixture : IAsyncDisposable
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _source;

    /// <summary>Starts an isolated operation database without changing the shared base-image fixture.</summary>
    /// <param name="includeNadconGrid">Whether to install the pinned NOAA grid for NADCON operations.</param>
    public async Task InitializeAsync(bool includeNadconGrid = true)
    {
        // Pin both the PROJ operation database and its available grids. In particular,
        // CI's shared PostGIS database may carry a legacy conus grid that changes
        // the default operation; neither profile may inherit it or download grids.
        const string image = "postgis/postgis:18-3.6@sha256:60f6ad1d21ea86a67d47780b9a0d1e1d200500f62b19293fa834d0dea80b8677";
        var builder = new PostgreSqlBuilder().WithImage(image)
            .WithDatabase("datum_fixture").WithUsername("test").WithPassword("test")
            .WithEnvironment("PROJ_DATA", "/opt/honua-datum-proj")
            .WithEnvironment("PROJ_LIB", "/opt/honua-datum-proj")
            .WithEnvironment("PROJ_NETWORK", "OFF")
            // Prepare this root-owned directory before the image drops to postgres.
            .WithEntrypoint("/bin/sh", "-c")
            // PostgreSqlBuilder appends its own postgres flags by default. Replace
            // them so sh receives this script as its first and only command.
            .WithCommand(new OverwriteEnumerable<string>(["mkdir -p /opt/honua-datum-proj && " +
                "cp /usr/share/proj/proj.db /opt/honua-datum-proj/proj.db && " +
                "exec /usr/local/bin/docker-entrypoint.sh postgres"]));
        if (includeNadconGrid)
        {
            await using var resource = typeof(DatumGridPostgresFixture).Assembly.GetManifestResourceStream(
                "Honua.TestKit.TestData.Proj.us_noaa_conus.tif")
                ?? throw new InvalidOperationException("The pinned NADCON fixture is missing.");
            using var bytes = new MemoryStream();
            await resource.CopyToAsync(bytes);
            builder = builder.WithResourceMapping(bytes.ToArray(), "/opt/honua-datum-proj/us_noaa_conus.tif");
        }
        _container = builder.Build();
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
