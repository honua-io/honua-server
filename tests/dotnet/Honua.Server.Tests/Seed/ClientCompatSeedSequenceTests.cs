// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Testcontainers.PostgreSql;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Honua.Server.Tests.Seed;

[Protocol(Protocols.Infrastructure)]
public sealed class ClientCompatSeedSequenceTests
{
    private const string PostgisImage = "postgis/postgis:16-3.4";

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ClientCompatSeedSequence_WithBrowserCompatSeed_RefreshesMetadataV2Snapshots()
    {
        await using var container = new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithDatabase("honua_seed_regression")
            .WithUsername("postgres")
            .WithPassword("compat_password")
            .WithEnvironment("POSTGIS_GDAL_ENABLED_DRIVERS", "ENABLE_ALL")
            .Build();

        await container.StartAsync();

        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await ExecuteSqlFileAsync(dataSource, RepositoryPaths.Resolve("tests", "seed", "client-compat-v1.sql"));
        await ExecuteYamlSqlAsync(dataSource, RepositoryPaths.Resolve("tests", "seed", "browser-compat.yaml"));

        var snapshots = await ReadCurrentSnapshotsAsync(dataSource);

        snapshots.Keys.Should().BeEquivalentTo(["default", "Development", "Test"]);

        foreach (var snapshot in snapshots.Values)
        {
            using var document = JsonDocument.Parse(snapshot);
            AssertContainsBrowserCompatibilityMetadata(document.RootElement);
        }
    }

    private static async Task ExecuteSqlFileAsync(NpgsqlDataSource dataSource, string path)
    {
        await ExecuteSqlAsync(dataSource, await File.ReadAllTextAsync(path));
    }

    private static async Task ExecuteYamlSqlAsync(NpgsqlDataSource dataSource, string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var seed = deserializer.Deserialize<YamlSqlSeed>(await File.ReadAllTextAsync(path));

        seed.Should().NotBeNull();
        seed!.Version.Should().Be(1);
        seed.Sql.Should().NotBeNullOrEmpty();

        foreach (var sql in seed.Sql)
        {
            await ExecuteSqlAsync(dataSource, sql);
        }
    }

    private static async Task ExecuteSqlAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, string>> ReadCurrentSnapshotsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.environment, s.document::text
            FROM honua.metadata_v2_current c
            JOIN honua.metadata_v2_snapshots s
              ON s.environment = c.environment
             AND s.revision = c.revision
            ORDER BY s.environment;
            """;

        var snapshots = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            snapshots.Add(reader.GetString(0), reader.GetString(1));
        }

        return snapshots;
    }

    private static void AssertContainsBrowserCompatibilityMetadata(JsonElement snapshot)
    {
        var serviceNames = snapshot
            .GetProperty("services")
            .EnumerateArray()
            .Select(service => service.GetProperty("metadata").GetProperty("name").GetString())
            .ToArray();

        serviceNames.Should().Contain("browser_compat");

        var services = snapshot
            .GetProperty("services")
            .EnumerateArray()
            .Select(service => new
            {
                Id = service.GetProperty("metadata").GetProperty("id").GetString(),
                Type = service.GetProperty("serviceType").GetString()
            })
            .ToArray();

        services.Should().Contain(service =>
            service.Id == "svc-browser-compat-feature" &&
            service.Type == "esri-feature-service");
        services.Should().Contain(service =>
            service.Id == "svc-browser-compat-map" &&
            service.Type == "esri-map-service");

        var publishedBrowserLayers = snapshot
            .GetProperty("publications")
            .EnumerateArray()
            .Where(publication =>
            {
                var serviceId = publication.GetProperty("serviceId").GetString();
                return serviceId is "svc-browser-compat-feature" or "svc-browser-compat-map";
            })
            .Select(publication => publication.GetProperty("layerIndex").GetInt32())
            .Distinct()
            .ToArray();

        publishedBrowserLayers.Should().Contain([2000, 2001, 2002]);
    }

    private sealed class YamlSqlSeed
    {
        public int Version { get; set; }

        public List<string> Sql { get; set; } = [];
    }
}
