// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
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
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                // The test fixture replaces the provider after AddServerFeatures
                // has already evaluated its capability-gated MCP roster. Repeat
                // that production roster entry only after the real import service
                // is present; the tool still uses the production import pipeline.
                services.Any(descriptor => descriptor.ServiceType == typeof(IFileImportService))
                    .Should().BeTrue("the Postgres provider must supply the real import pipeline");
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, IngestDatasetTool>());
            })
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Operations:Policy:Enabled", "true");
                builder.UseSetting("Operations:Policy:DefaultDecision", "Deny");
                builder.UseSetting("Operations:Policy:Rules:0:Role", "admin");
                builder.UseSetting("Operations:Policy:Rules:0:Decision", "Allow");
            });
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dataset = $"mcpingest{suffix}";
        var secondDataset = $"mcpambiguous{suffix}";
        var serviceName = $"mcpingest{suffix}";
        var aliasName = $"mcpalias{suffix}";
        var initialized = false;
        string? testSchema = null;
        try
        {
            await fixture.InitializeAsync();
            testSchema = fixture.CurrentSchema
                ?? throw new InvalidOperationException("The fixture schema was not initialized.");
            initialized = true;
            using var admin = fixture.CreateAdminClient();
            using var scope = fixture.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
            var fixtureConnection = await registry.GetConnectionByNameAsync("test");
            fixtureConnection.Should().NotBeNull("the fixture registers the catalog through the real secure registry");

            var ingested = await CallToolAsync(admin, "honua_ingest_dataset", new
            {
                format = "geojson", data = GeoJson, datasetName = dataset
            });
            ingested.GetProperty("success").GetBoolean().Should().BeTrue(ingested.GetRawText());
            ingested.GetProperty("rowCount").GetInt32().Should().Be(1);
            var connectionName = ingested.GetProperty("connectionId").GetString();
            connectionName.Should().Be(fixtureConnection!.Name);
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
            var builder = new NpgsqlConnectionStringBuilder(fixture.Postgres.ConnectionString);
            using var created = await admin.PostAsJsonAsync("/api/v1/admin/connections", new
            {
                name = aliasName,
                host = builder.Host,
                port = builder.Port,
                databaseName = builder.Database,
                username = builder.Username,
                password = builder.Password,
                provider = "postgis",
                sslRequired = false,
                sslMode = "Disable",
            });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            var alias = await registry.GetConnectionByNameAsync(aliasName);
            alias.Should().NotBeNull("the API should persist its allocated registration");

            var ambiguous = await CallToolAsync(admin, "honua_ingest_dataset", new
            {
                format = "geojson", data = GeoJson, datasetName = secondDataset
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
                if (initialized)
                {
                    // The fixture owns its "test" registration. Clean only this
                    // test's service, published layer, admin-created alias and
                    // deterministic import tables, even if publish failed after
                    // a successful ingest. DDL is serialized with other fixtures.
                    await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
                    {
                        await using var connection = await fixture.Postgres.GetConnectionAsync(testSchema!);
                        await using var command = connection.CreateCommand();
                        command.CommandText = $"""
                            DELETE FROM {QuoteIdentifier(testSchema!)}.features
                            WHERE layer_id IN (SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
                            DELETE FROM honua.layers
                            WHERE layer_id IN (SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
                            DELETE FROM honua.services WHERE service_name = @serviceName;
                            DELETE FROM honua.data_connections WHERE name = @aliasName;
                            DROP TABLE IF EXISTS honua_data.{QuoteIdentifier("imported_" + dataset)} CASCADE;
                            DROP TABLE IF EXISTS honua_data.{QuoteIdentifier("imported_" + dataset + "__staging")} CASCADE;
                            DROP TABLE IF EXISTS honua_data.{QuoteIdentifier("imported_" + secondDataset)} CASCADE;
                            DROP TABLE IF EXISTS honua_data.{QuoteIdentifier("imported_" + secondDataset + "__staging")} CASCADE;
                            """;
                        command.Parameters.AddWithValue("serviceName", serviceName);
                        command.Parameters.AddWithValue("aliasName", aliasName);
                        await command.ExecuteNonQueryAsync();
                    });
                }
            }
            finally
            {
                await fixture.DisposeAsync();
            }
        }
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
