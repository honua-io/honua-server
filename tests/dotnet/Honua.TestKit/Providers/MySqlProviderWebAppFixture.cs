// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace Honua.TestKit.Providers;

/// <summary>
/// Web-app fixture that boots a real <see cref="WebApplicationFactory{TEntryPoint}"/> host
/// with <c>DataSource:Provider=mysql</c> against a Testcontainers <c>mysql:8</c> instance
/// seeded with <see cref="ProviderSmokeData"/> (honua-server#2947).
/// </summary>
/// <remarks>
/// MySql is a primary-provider replacement (mutually exclusive with Postgres at
/// <c>InfrastructureCompositionRoot</c>'s <c>DataSource:Provider</c> switch), so this
/// fixture builds its own <see cref="WebApplicationFactory{TEntryPoint}"/> from scratch
/// rather than reusing <see cref="WebAppFixture"/>, which is hard-wired to Postgres. Layer
/// mapping (table/column names) is static, config-time (<c>MySql:Layers:0:*</c>), so the
/// Metadata v2 graph built by <see cref="ProviderSmokeGraph"/> is used only for protocol
/// discovery/schema surfaces (OGC API Features collection, OData $metadata, FeatureServer
/// layer metadata) — both must agree on the layer id (ProviderSmokeGraph.LayerId) / table "parcels".
/// </remarks>
public sealed class MySqlProviderWebAppFixture : IAsyncLifetime
{
    private MySqlContainer _container = null!;
    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>HTTP client bound to the fixture's MySql-backed host.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>The in-memory Metadata v2 graph provider installed into the test host.</summary>
    public TestMetadataV2GraphProvider GraphProvider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new MySqlBuilder()
            .WithImage("mysql:8.0.36")
            .WithDatabase("honua_smoke")
            .WithUsername("honua")
            .WithPassword("honua-smoke-test")
            .Build();
        await _container.StartAsync().ConfigureAwait(false);

        await SeedAsync(_container.GetConnectionString()).ConfigureAwait(false);

        var graph = ProviderSmokeGraph.Build(locator: "parcels");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "true");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
            builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");

            ProviderSmokeHostConfiguration.ApplySettings(builder, new Dictionary<string, string?>
            {
                ["DataSource:Provider"] = "mysql",
                ["MySql:ConnectionString"] = _container.GetConnectionString(),
                ["MySql:EngineFlavor"] = "Mysql",
                ["MySql:Layers:0:Id"] = "1",
                ["MySql:Layers:0:Name"] = "parcels",
                ["MySql:Layers:0:Table"] = "parcels",
                ["MySql:Layers:0:GeometryColumn"] = "geom",
                ["MySql:Layers:0:PrimaryKeyColumn"] = "objectid",
                ["MySql:Layers:0:Srid"] = "4326",
                ["MySql:Layers:0:GeometryType"] = "Point",
                ["MySql:Layers:0:Attributes:0"] = "name",
                ["MySql:Layers:0:Attributes:1"] = "area",
                ["MySql:Layers:0:Attributes:2"] = "type",
                ["MySql:Services:0:Name"] = ProviderSmokeGraph.ServiceName,
                ["MySql:Services:0:LayerIds:0"] = "1",
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.RemoveAll<IMetadataV2GraphStore>();
                GraphProvider = new TestMetadataV2GraphProvider(graph);
                services.AddSingleton(GraphProvider);
                services.AddSingleton<IMetadataV2GraphProvider>(sp => sp.GetRequiredService<TestMetadataV2GraphProvider>());
                services.AddSingleton<IMetadataV2GraphStore>(sp => sp.GetRequiredService<TestMetadataV2GraphProvider>());
            });
        });

        Client = _factory.CreateClient();
        Client.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var dataSource = new MySqlDataSourceBuilder(connectionString).Build();

        await using (var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE parcels (
                    objectid BIGINT PRIMARY KEY,
                    geom POINT NOT NULL SRID 4326,
                    name VARCHAR(64),
                    area DOUBLE,
                    type VARCHAR(32),
                    SPATIAL INDEX(geom)
                ) ENGINE=InnoDB
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        foreach (var parcel in ProviderSmokeData.Parcels)
        {
            await using var connection = await dataSource.OpenConnectionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO parcels (objectid, geom, name, area, type)
                VALUES (@id, ST_SRID(POINT(@lon, @lat), 4326), @name, @area, @type)
                """;
            command.Parameters.AddWithValue("@id", parcel.Id);
            command.Parameters.AddWithValue("@lon", parcel.Longitude);
            command.Parameters.AddWithValue("@lat", parcel.Latitude);
            command.Parameters.AddWithValue("@name", parcel.Name);
            command.Parameters.AddWithValue("@area", parcel.Area);
            command.Parameters.AddWithValue("@type", parcel.Type);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
