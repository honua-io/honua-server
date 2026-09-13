// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Server.Tests.Seed;

/// <summary>
/// Regression for honua-io/honua-server#3384: the live demo canary reads HTTP 500 from
/// <c>GET /stac/collections/90810/items</c> and <c>POST /stac/search</c> while collection
/// discovery and collection metadata stay 200.
///
/// The deployed cause is database state, not the STAC handlers: the demo database lost
/// <c>honua.features</c> (PostgreSQL <c>42P01</c>) but kept its <c>feature_changes</c> journal,
/// and the Metadata v2 graph still publishes the collection. This test drives the real
/// <c>tests/seed/demo-stac-imagery-v1.sql</c> over a database migrated by the production DbUp
/// runner, then pins both halves of that contract through a host bound to the database:
/// <list type="bullet">
///   <item>seeded, collection 90810 serves exactly the four seed scenes through the canary's own
///   items and search requests, with geometry, datetime and properties equal to the literals the
///   seed inserts (restated here, not read back from the server);</item>
///   <item>with the relation dropped and the journal retained, metadata stays 200 while items and
///   search fail loudly with 500 (never an empty 200 page a canary would pass), and re-running the
///   seed refuses without recreating the relation, because the journal cannot be rebaselined.</item>
/// </list>
/// </summary>
[Collection("Database.CoreEndpoints")]
[Protocol(TestProtocols.Stac)]
public sealed class DemoStacSeedMigratedDatabaseTests
{
    private const string MetadataEnvironment = "Production";
    private const string CollectionId = "90810";
    private const string CollectionPath = "/stac/collections/" + CollectionId;
    private const string SearchPath = "/stac/search";

    /// <summary>The Maui Reef Watch scenes the seed inserts under layer 90810.</summary>
    private static readonly ExpectedScene[] _reefWatchScenes =
    [
        new("9081001", -156.4730, 20.8910, "S2-MAUI-Reef-01", "2026-03-01T20:30:00Z", 96, 5.2, 142.0, "sentinel-2a"),
        new("9081002", -156.3120, 20.7460, "S2-MAUI-Reef-02", "2026-03-04T20:30:00Z", 93, 8.9, 144.7, "sentinel-2b"),
        new("9081003", -156.6650, 20.9540, "S2-MAUI-Reef-03", "2026-03-07T20:30:00Z", 90, 12.4, 147.1, "sentinel-2a"),
        new("9081004", -156.4470, 20.6320, "S2-MAUI-Reef-04", "2026-03-10T20:30:00Z", 87, 16.8, 150.5, "sentinel-2b"),
    ];

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /stac/collections/{collectionId}")]
    [Endpoint("GET /stac/collections/{collectionId}/items")]
    [Endpoint("POST /stac/search")]
    public async Task DemoStacSeed_OnServerMigratedDatabase_ServesCollection90810ItemsAndSearch()
    {
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        var connectionString = await postgres.CreateIsolatedDatabaseAsync(nameof(DemoStacSeedMigratedDatabaseTests));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;

        try
        {
            var runner = new PostgresDatabaseMigrationRunner(
                new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
                ServerCoreSchemaMigrations.Manifest);
            var migrations = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
            migrations.Successful.Should().BeTrue(
                $"the demo database is migrated by the server before the seed runs. Error: {migrations.ErrorMessage}");

            var seed = await RenderSeedAsync();

            // The governed recovery re-runs the seed, so a healthy re-run must stay applicable.
            await ExecuteAsync(connectionString, seed);
            await ExecuteAsync(connectionString, seed);

            await using var factory = CreateHost(connectionString);
            // Anonymous, like the public canary: the seeded service and resources allow it.
            using var client = factory.CreateClient();

            // --- seeded: the canary's items probe (limit=2) and the full collection -----------
            (await SendAsync(client, HttpMethod.Get, CollectionPath)).StatusCode.Should().Be(HttpStatusCode.OK);

            using (var canaryItems = await ReadFeatureCollectionAsync(client, HttpMethod.Get, CollectionPath + "/items?limit=2"))
            {
                AssertPage(canaryItems.RootElement, expectedReturned: 2, expectNextLink: true);
            }

            using (var allItems = await ReadFeatureCollectionAsync(client, HttpMethod.Get, CollectionPath + "/items?limit=25"))
            {
                AssertPage(allItems.RootElement, expectedReturned: 4, expectNextLink: false);
                AssertScenes(allItems.RootElement, _reefWatchScenes);
            }

            // --- seeded: the canary's search probe and a full, then spatially bounded, search ---
            using (var canarySearch = await ReadFeatureCollectionAsync(
                client, HttpMethod.Post, SearchPath, $$"""{"collections":["{{CollectionId}}"],"limit":2}"""))
            {
                AssertPage(canarySearch.RootElement, expectedReturned: 2, expectNextLink: true);
            }

            using (var allSearch = await ReadFeatureCollectionAsync(
                client, HttpMethod.Post, SearchPath, $$"""{"collections":["{{CollectionId}}"],"limit":25}"""))
            {
                AssertPage(allSearch.RootElement, expectedReturned: 4, expectNextLink: false);
                AssertScenes(allSearch.RootElement, _reefWatchScenes);
            }

            // This box holds Reef-01 and Reef-03 plus Coastal-01 of collection 90820, which the
            // collections filter must exclude.
            using (var boundedSearch = await ReadFeatureCollectionAsync(
                client,
                HttpMethod.Post,
                SearchPath,
                $$"""{"collections":["{{CollectionId}}"],"bbox":[-156.70,20.85,-156.45,20.96],"limit":25}"""))
            {
                AssertScenes(boundedSearch.RootElement, [_reefWatchScenes[0], _reefWatchScenes[2]]);
            }

            // --- the deployed state: relation lost, change journal retained ---------------------
            (await ScalarAsync<long>(connectionString, "SELECT count(*) FROM honua.feature_changes"))
                .Should().BeGreaterThan(0, "the seed's inserts are journaled, as on the live demo database");
            await ExecuteAsync(connectionString, "DROP TABLE honua.features CASCADE;");

            (await SendAsync(client, HttpMethod.Get, CollectionPath)).StatusCode.Should().Be(
                HttpStatusCode.OK, "collection metadata is served from the Metadata v2 graph, not the relation");
            await AssertServerErrorAsync(client, HttpMethod.Get, CollectionPath + "/items?limit=2");
            await AssertServerErrorAsync(
                client, HttpMethod.Post, SearchPath, $$"""{"collections":["{{CollectionId}}"],"limit":2}""");

            var reseed = () => ExecuteAsync(connectionString, seed);
            var refusal = await reseed.Should().ThrowAsync<PostgresException>();
            refusal.Which.SqlState.Should().Be("55000");
            refusal.Which.MessageText.Should().Contain(
                "operator rebaseline",
                "the retained journal cannot reconstruct the lost rows, so the seed must not paper over it");
            (await ScalarAsync<bool>(connectionString, "SELECT to_regclass('honua.features') IS NULL"))
                .Should().BeTrue("a refused recovery must not leave an empty relation that would turn the 500 into an empty 200");
            await AssertServerErrorAsync(client, HttpMethod.Get, CollectionPath + "/items?limit=2");
        }
        finally
        {
            await postgres.DropDatabaseAsync(databaseName);
            await postgres.DisposeAsync();
        }
    }

    private static WebApplicationFactory<Program> CreateHost(string connectionString)
        => ConfiguredWebApplicationFactory.Create(
            builder =>
            {
                // Migrations are already applied by the runner above.
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(
                        WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                            connectionString,
                            new Dictionary<string, string?>
                            {
                                ["HONUA_ADMIN_PASSWORD"] = WebAppFixture.SharedAdminPassword,
                                ["Metadata:Environment"] = MetadataEnvironment
                            })));
            },
            "Test");

    /// <summary>
    /// Renders the psql script for the ADO.NET driver: drops the psql preamble and binds the
    /// <c>env</c> and <c>schema</c> variables the managed demo executor passes.
    /// </summary>
    private static async Task<string> RenderSeedAsync()
    {
        var source = await File.ReadAllTextAsync(RepositoryPaths.Resolve("tests", "seed", "demo-stac-imagery-v1.sql"));
        var begin = source.IndexOf("\nBEGIN;", StringComparison.Ordinal);
        begin.Should().BeGreaterThan(0, "the demo STAC seed runs in one transaction");

        var rendered = source[(begin + 1)..]
            .Replace(":\"schema\"", "\"honua\"", StringComparison.Ordinal)
            .Replace(":'schema'", "'honua'", StringComparison.Ordinal)
            .Replace(":'env'", $"'{MetadataEnvironment}'", StringComparison.Ordinal);
        rendered.Should().NotContain("\\set").And.NotContain(":'", "every psql substitution must be bound");
        return rendered;
    }

    private static void AssertPage(JsonElement page, int expectedReturned, bool expectNextLink)
    {
        page.GetProperty("type").GetString().Should().Be("FeatureCollection");
        page.GetProperty("numberMatched").GetInt32().Should().Be(_reefWatchScenes.Length);
        page.GetProperty("numberReturned").GetInt32().Should().Be(expectedReturned);

        var features = page.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(expectedReturned);
        features.Select(feature => feature.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems()
            .And.BeSubsetOf(_reefWatchScenes.Select(scene => scene.Id));
        features.Should().OnlyContain(feature => feature.GetProperty("collection").GetString() == CollectionId);

        page.GetProperty("links").EnumerateArray()
            .Any(link => link.GetProperty("rel").GetString() == "next")
            .Should().Be(expectNextLink);
    }

    private static void AssertScenes(JsonElement page, IReadOnlyList<ExpectedScene> expected)
    {
        var features = page.GetProperty("features").EnumerateArray()
            .ToDictionary(feature => feature.GetProperty("id").GetString()!, StringComparer.Ordinal);
        features.Keys.Should().BeEquivalentTo(expected.Select(scene => scene.Id));

        foreach (var scene in expected)
        {
            var feature = features[scene.Id];
            feature.GetProperty("collection").GetString().Should().Be(CollectionId);

            var geometry = feature.GetProperty("geometry");
            geometry.GetProperty("type").GetString().Should().Be("Point");
            var coordinates = geometry.GetProperty("coordinates").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            coordinates.Should().HaveCount(2);
            coordinates[0].Should().BeApproximately(scene.Longitude, 1e-9, $"{scene.Id} longitude");
            coordinates[1].Should().BeApproximately(scene.Latitude, 1e-9, $"{scene.Id} latitude");

            var properties = feature.GetProperty("properties");
            DateTimeOffset.Parse(properties.GetProperty("datetime").GetString()!, CultureInfo.InvariantCulture)
                .Should().Be(DateTimeOffset.Parse(scene.ObservedAt, CultureInfo.InvariantCulture), $"{scene.Id} datetime");
            properties.GetProperty("name").GetString().Should().Be(scene.Name);
            properties.GetProperty("quality_score").GetInt32().Should().Be(scene.QualityScore);
            properties.GetProperty("eo:cloud_cover").GetDouble().Should().BeApproximately(scene.CloudCover, 1e-9);
            properties.GetProperty("view:sun_azimuth").GetDouble().Should().BeApproximately(scene.SunAzimuth, 1e-9);
            properties.GetProperty("platform").GetString().Should().Be(scene.Platform);
        }
    }

    private static async Task<JsonDocument> ReadFeatureCollectionAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? jsonBody = null)
    {
        using var response = await SendAsync(client, method, path, jsonBody);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"{method} {path} returned {(int)response.StatusCode}: {Truncate(payload)}");
        return JsonDocument.Parse(payload);
    }

    private static async Task AssertServerErrorAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? jsonBody = null)
    {
        using var response = await SendAsync(client, method, path, jsonBody);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.InternalServerError,
            $"{method} {path} over a lost feature relation must fail loudly, not answer: {Truncate(payload)}");
        using var problem = JsonDocument.Parse(payload);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        problem.RootElement.TryGetProperty("features", out _).Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? jsonBody = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500];

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 180;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed record ExpectedScene(
        string Id,
        double Longitude,
        double Latitude,
        string Name,
        string ObservedAt,
        int QualityScore,
        double CloudCover,
        double SunAzimuth,
        string Platform);
}
