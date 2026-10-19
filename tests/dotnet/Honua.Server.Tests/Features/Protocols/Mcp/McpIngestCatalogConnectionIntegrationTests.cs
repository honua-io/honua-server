// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Real admin and MCP HTTP surfaces over the production secure-connection
/// registry, Npgsql matcher, import service and publish executor.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpIngestCatalogConnectionIntegrationTests
{
    private const string GeoJson =
        """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"Honolulu"},"geometry":{"type":"Point","coordinates":[-157.86,21.31]}}]}""";

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /mcp tools/call honua_ingest_dataset")]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task RegisteredCatalogConnection_IngestHintFeedsPublish_AndSecondRegistrationIsAmbiguous()
    {
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dataset = $"mcpingest{suffix}";
        var secondDataset = $"mcpambiguous{suffix}";
        var serviceName = $"mcpingest{suffix}";
        var catalogName = $"mcpcatalog{suffix}";
        var aliasName = $"mcpalias{suffix}";
        string? databaseName = null;
        try
        {
            // A dedicated database gives the real registry one catalog connection
            // and installs the same role search path as docker/init-db.sql. The
            // process-wide WebAppFixture "test" row cannot be disabled safely.
            var isolatedConnection = await postgres.CreateIsolatedDatabaseAsync(nameof(McpIngestCatalogConnectionIntegrationTests));
            var connectionBuilder = new NpgsqlConnectionStringBuilder(isolatedConnection);
            databaseName = connectionBuilder.Database;
            await SetOwnedDatabaseSearchPathAsync(isolatedConnection);
            connectionBuilder.SearchPath = "honua,public";
            var catalogConnection = connectionBuilder.ConnectionString;

            var migrationRunner = new PostgresDatabaseMigrationRunner(
                new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
                ServerCoreSchemaMigrations.Manifest);
            var migrations = await migrationRunner.RunMigrationsAsync(catalogConnection, typeof(Program).Assembly);
            migrations.Successful.Should().BeTrue(migrations.ErrorMessage);

            await using var factory = CreateHost(catalogConnection);
            using var admin = factory.CreateClient();
            admin.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
            using var scope = factory.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
            (await registry.GetActiveConnectionsAsync()).Should().BeEmpty("this catalog is an owned isolated database");
            var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
            store.Should().BeOfType<PostgresMetadataV2GraphStore>();
            await store.SaveAsync(new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow
            }, expectedEtag: null);

            using var registered = await admin.PostAsJsonAsync("/api/v1/admin/connections", new
            {
                name = catalogName,
                host = connectionBuilder.Host,
                port = connectionBuilder.Port,
                databaseName = connectionBuilder.Database,
                username = connectionBuilder.Username,
                password = connectionBuilder.Password,
                provider = "postgis",
                sslRequired = false,
                sslMode = "Disable",
            });
            registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
            var catalogRegistration = await registry.GetConnectionByNameAsync(catalogName);
            catalogRegistration.Should().NotBeNull("the supported admin route must persist the actual connection");

            var ingested = await CallToolAsync(admin, "honua_ingest_dataset", new
            {
                format = "geojson",
                data = GeoJson,
                datasetName = dataset
            });
            ingested.GetProperty("success").GetBoolean().Should().BeTrue(ingested.GetRawText());
            ingested.GetProperty("rowCount").GetInt32().Should().Be(1);
            var connectionName = ingested.GetProperty("connectionId").GetString();
            connectionName.Should().Be(catalogRegistration!.Name);
            var schema = ingested.GetProperty("schema").GetString();
            var table = ingested.GetProperty("table").GetString();

            var published = await CallToolAsync(admin, "honua_publish_service", new
            {
                connectionId = connectionName,
                schema,
                table,
                geometryColumn = ingested.GetProperty("geometryColumn").GetString(),
                primaryKey = ingested.GetProperty("primaryKey").GetString(),
                srid = ingested.GetProperty("srid").GetInt32(),
                layerName = "Honolulu",
                serviceName,
            });
            published.GetProperty("status").GetString().Should().Be("Completed", published.GetRawText());
            published.GetProperty("layerId").GetString().Should().NotBeNullOrWhiteSpace();

            // The operation handle carries a physical LayerId. Query the
            // public FeatureServer using its own published layer index.
            using var serviceResponse = await admin.GetAsync($"/rest/services/{serviceName}/FeatureServer?f=json");
            var servicePayload = await serviceResponse.Content.ReadAsStringAsync();
            serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK, servicePayload);
            using var serviceMetadata = JsonDocument.Parse(servicePayload);
            var publicLayerId = serviceMetadata.RootElement.GetProperty("layers").EnumerateArray()
                .Single(layer => layer.GetProperty("name").GetString() == "Honolulu")
                .GetProperty("id").GetInt32();
            using var queryResponse = await admin.GetAsync(
                $"/rest/services/{serviceName}/FeatureServer/{publicLayerId}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true");
            var queryPayload = await queryResponse.Content.ReadAsStringAsync();
            queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, queryPayload);
            using var query = JsonDocument.Parse(queryPayload);
            var features = query.RootElement.GetProperty("features");
            features.GetArrayLength().Should().Be(1);
            using var properties = JsonDocument.Parse(
                features[0].GetProperty("attributes").GetProperty("properties").GetString()!);
            properties.RootElement.GetProperty("name").GetString().Should().Be("Honolulu");
            features[0].GetProperty("geometry").GetProperty("x").GetDouble().Should().BeApproximately(-157.86, 1e-6);
            features[0].GetProperty("geometry").GetProperty("y").GetDouble().Should().BeApproximately(21.31, 1e-6);

            // The supported admin route adds a second valid registration for
            // this same catalog. Ingest must stop suggesting an arbitrary one.
            using var created = await admin.PostAsJsonAsync("/api/v1/admin/connections", new
            {
                name = aliasName,
                host = connectionBuilder.Host,
                port = connectionBuilder.Port,
                databaseName = connectionBuilder.Database,
                username = connectionBuilder.Username,
                password = connectionBuilder.Password,
                provider = "postgis",
                sslRequired = false,
                sslMode = "Disable",
            });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var alias = await registry.GetConnectionByNameAsync(aliasName);
            alias.Should().NotBeNull("the API should persist its allocated registration");

            var ambiguous = await CallToolAsync(admin, "honua_ingest_dataset", new
            {
                format = "geojson",
                data = GeoJson,
                datasetName = secondDataset
            });
            ambiguous.GetProperty("success").GetBoolean().Should().BeTrue();
            ambiguous.TryGetProperty("connectionId", out _).Should().BeFalse();
            ambiguous.GetProperty("warnings").EnumerateArray()
                .Should().Contain(w => w.GetString()!.Contains("More than one registered", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (databaseName is not null)
                {
                    await postgres.DropDatabaseAsync(databaseName);
                }
            }
            finally
            {
                await postgres.DisposeAsync();
            }
        }
    }

    private static async Task SetOwnedDatabaseSearchPathAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA IF NOT EXISTS honua;
            DO $$
            BEGIN
                EXECUTE format(
                    'ALTER ROLE %I IN DATABASE %I SET search_path = honua, public',
                    current_user,
                    current_database());
            END $$;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static WebApplicationFactory<Program> CreateHost(string catalogConnection)
        => ConfiguredWebApplicationFactory.Create(builder =>
        {
            builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("Licensing:Mode", "Disabled");
            builder.UseSetting("Operations:Policy:Enabled", "true");
            builder.UseSetting("Operations:Policy:DefaultDecision", "Deny");
            builder.UseSetting("Operations:Policy:Rules:0:Role", "admin");
            builder.UseSetting("Operations:Policy:Rules:0:Decision", "Allow");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                        catalogConnection,
                        new Dictionary<string, string?> { ["Metadata:Environment"] = "Test" })));
            builder.ConfigureTestServices(services =>
            {
                WebAppFixturePostgresWiringMixin.RemovePostgresBackedServices(services);
                WebAppFixturePostgresWiringMixin.RemoveBackgroundPollers(services);
                Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
                    services,
                    WebAppFixturePostgresWiringMixin.BuildPostgresTestConfiguration(
                        catalogConnection,
                        new Dictionary<string, string?> { ["Metadata:Environment"] = "Test" }),
                    ServerCoreSchemaMigrations.Manifest);
                WebAppFixturePostgresWiringMixin.OverrideNonMultiplexingDataSource(services, catalogConnection);
                Honua.Infrastructure.Rendering.VectorAwareRasterMapRendererServiceCollectionExtensions
                    .AddVectorAwareRasterMapRendering(services);
                services.Any(descriptor => descriptor.ServiceType == typeof(IFileImportService))
                    .Should().BeTrue("the production Postgres import pipeline must be registered");
                // Test skips infrastructure before AddServerFeatures evaluates its
                // capability-gated MCP roster. Re-add the actual ingest tool after
                // registering the real Postgres importer, as ImportAuthorizationParityTests does.
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, IngestDatasetTool>());
            });
        }, "Test");

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = "catalog-ingest",
            method = "tools/call",
            @params = new { name, arguments },
        });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(payload);
        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse(payload);
        }

        return result.GetProperty("structuredContent").Clone();
    }
}
