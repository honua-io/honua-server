// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DuckDB.NET.Data;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Honua.TestKit.Providers;

/// <summary>
/// Web-app fixture that boots a real <see cref="WebApplicationFactory{TEntryPoint}"/> host
/// with <c>DataSource:Provider=duckdb</c> against a standalone, file-backed DuckDB database
/// seeded with <see cref="ProviderSmokeData"/> (honua-server#2947). DuckDB is embedded — no
/// Testcontainer is needed; the fixture creates a temp <c>.duckdb</c> file, installs/loads
/// the <c>spatial</c> extension, and seeds it directly via <see cref="DuckDBConnection"/>
/// before the host opens the same file read-only (matching the documented production
/// default, <c>DuckDB:ReadOnly=true</c>).
/// </summary>
/// <remarks>
/// DuckDB is a primary-provider replacement (mutually exclusive with Postgres at
/// <c>InfrastructureCompositionRoot</c>'s <c>DataSource:Provider</c> switch), so this
/// fixture builds its own <see cref="WebApplicationFactory{TEntryPoint}"/> from scratch
/// rather than reusing <see cref="WebAppFixture"/>, which is hard-wired to Postgres. Layer
/// mapping (table/column names) is static, config-time (<c>DuckDB:Layers:0:*</c>), so the
/// Metadata v2 graph built by <see cref="ProviderSmokeGraph"/> is used only for protocol
/// discovery/schema surfaces (OGC API Features collection, OData $metadata, FeatureServer
/// layer metadata) — both must agree on the layer id (ProviderSmokeGraph.LayerId) / table "parcels".
/// </remarks>
public sealed class DuckDbProviderWebAppFixture : IAsyncLifetime
{
    private string _dbPath = null!;
    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>HTTP client bound to the fixture's DuckDB-backed host.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>The in-memory Metadata v2 graph provider installed into the test host.</summary>
    public TestMetadataV2GraphProvider GraphProvider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"honua_provider_smoke_{Guid.NewGuid():N}.duckdb");
        await SeedAsync(_dbPath).ConfigureAwait(false);

        var graph = ProviderSmokeGraph.Build(locator: "parcels");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "true");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
            builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");

            ProviderSmokeHostConfiguration.ApplySettings(builder, new Dictionary<string, string?>
            {
                ["DataSource:Provider"] = "duckdb",
                ["DuckDB:DatabasePath"] = _dbPath,
                ["DuckDB:ReadOnly"] = "true",
                ["DuckDB:Layers:0:Id"] = "1",
                ["DuckDB:Layers:0:Table"] = "parcels",
                ["DuckDB:Layers:0:GeometryColumn"] = "geom",
                ["DuckDB:Layers:0:ObjectIdColumn"] = "objectid",
                ["DuckDB:Layers:0:Srid"] = "4326",
                ["DuckDB:Layers:0:GeometryType"] = "Point",
                ["DuckDB:Layers:0:Attributes:0"] = "name",
                ["DuckDB:Layers:0:Attributes:1"] = "area",
                ["DuckDB:Layers:0:Attributes:2"] = "type",
                ["DuckDB:Services:0:Name"] = ProviderSmokeGraph.ServiceName,
                ["DuckDB:Services:0:LayerIds:0"] = "1",
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

        if (!string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering file handle on Windows/CI runners
                // should not fail the test run.
            }
        }
    }

    private static async Task SeedAsync(string dbPath)
    {
        var connectionString = $"Data Source={dbPath}";
        await using var connection = new DuckDBConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await ExecuteAsync(connection, "INSTALL spatial; LOAD spatial;").ConfigureAwait(false);
        await ExecuteAsync(connection, """
            CREATE TABLE parcels (
                objectid BIGINT PRIMARY KEY,
                geom GEOMETRY,
                name VARCHAR,
                area DOUBLE,
                type VARCHAR
            )
            """).ConfigureAwait(false);

        foreach (var parcel in ProviderSmokeData.Parcels)
        {
            await ExecuteAsync(connection, FormattableString.Invariant(
                $"INSERT INTO parcels VALUES ({parcel.Id}, ST_Point({parcel.Longitude}, {parcel.Latitude}), '{parcel.Name}', {parcel.Area}, '{parcel.Type}')"))
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
