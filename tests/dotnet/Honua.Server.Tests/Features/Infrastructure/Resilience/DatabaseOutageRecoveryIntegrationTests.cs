// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Features.Infrastructure.Resilience;

[Protocol(ProtocolNames.FeatureServer, TestProtocols.Admin)]
public sealed class DatabaseOutageRecoveryIntegrationTests
{
    private const string PostgisImage = "postgis/postgis:18-3.6";
    private const string FeatureQueryBase = "/rest/services/test_service/FeatureServer/0/query?f=json&returnCountOnly=true&where=";
    private const string AdminExportPath = "/api/v1/admin/services/test_service/layers/0/export?format=csv";

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/export")]
    public async Task DatabaseOutage_WhenContainerStopsAndRestarts_ReturnsRetryable503ThenRecovers()
    {
        await using var container = BuildRestartableContainer();
        await container.StartAsync();

        var connectionString = BuildConnectionString(container.GetConnectionString());
        await PrepareDatabaseAsync(connectionString);

        await using var factory = CreateHost(connectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);

        await AssertHealthyCountAsync(client, "1%3D1");

        await container.StopAsync();
        await AssertOutageResponsesAsync(client);

        await container.StartAsync();
        await WaitForDatabaseRecoveryAsync(client);
    }

    private static PostgreSqlContainer BuildRestartableContainer()
        => new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithPortBinding(ReserveLoopbackPort(), 5432)
            .WithDatabase("honua_db_outage_test")
            .WithUsername("postgres")
            .WithPassword("outage_test_password")
            .WithLabel("honua.test.owner", "honua-server")
            .Build();

    private static string BuildConnectionString(string rawConnectionString)
        => new NpgsqlConnectionStringBuilder(rawConnectionString)
        {
            Timeout = 3,
            CommandTimeout = 10
        }.ToString();

    private static async Task PrepareDatabaseAsync(string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; "
            + "CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);
        var migrations = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        migrations.Successful.Should().BeTrue(migrations.ErrorMessage);
        var seedSql = await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "client-compat-v1.sql"));
        await ExecuteAsync(connectionString, seedSql);
    }

    private static async Task AssertHealthyCountAsync(HttpClient client, string whereClause)
    {
        using var response = await client.GetAsync(FeatureQueryBase + whereClause);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(10);
    }

    private static async Task AssertOutageResponsesAsync(HttpClient client)
    {
        using var geoResponse = await client.GetAsync(FeatureQueryBase + "2%3D2");
        await geoResponse.AssertGeoServicesErrorAsync(503);
        geoResponse.Headers.GetValues("Honua-Retryable").Should().ContainSingle().Which.Should().Be("true");
        geoResponse.Headers.Contains("Retry-After").Should().BeTrue();
        using var geoDoc = JsonDocument.Parse(await geoResponse.Content.ReadAsStringAsync());
        geoDoc.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean().Should().BeTrue();

        using var adminResponse = await client.GetAsync(AdminExportPath);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        adminResponse.Headers.GetValues("Honua-Retryable").Should().ContainSingle().Which.Should().Be("true");
        adminResponse.Headers.Contains("Retry-After").Should().BeTrue();
        using var adminDoc = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync());
        adminDoc.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        adminDoc.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
        adminDoc.RootElement.GetProperty("code").GetString().Should().Be("service_unavailable");
    }

    private static async Task WaitForDatabaseRecoveryAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync(FeatureQueryBase + "3%3D3");
            var payload = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK && !payload.Contains("\"error\"", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(payload);
                document.RootElement.GetProperty("count").GetInt32().Should().Be(10);
                return;
            }

            await Task.Delay(250);
        }

        await AssertHealthyCountAsync(client, "3%3D3");
    }

    private static WebApplicationFactory<Program> CreateHost(string connectionString)
        => ConfiguredWebApplicationFactory.Create(
            builder =>
            {
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("HONUA_REGISTER_TEST_INFRASTRUCTURE", "true");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(
                        WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                            connectionString,
                            new Dictionary<string, string?>
                            {
                                ["HONUA_ADMIN_PASSWORD"] = WebAppFixture.SharedAdminPassword,
                                ["HONUA_SKIP_MIGRATIONS"] = "false"
                            })));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDatabaseMigrationRunner>();
                    services.AddSingleton<IDatabaseMigrationRunner>(new PostgresDatabaseMigrationRunner(
                        new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
                        ServerCoreSchemaMigrations.Manifest));
                });
            },
            "Test");

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 180;
        await command.ExecuteNonQueryAsync();
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
