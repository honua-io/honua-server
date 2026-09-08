// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Seed;

/// <summary>
/// Certification-substrate regression for the Lambda GA lane (honua-io/honua-release#282).
///
/// <c>scripts/cloud/lambda-certification.md</c> bootstraps the cert PostGIS database with
/// <c>tests/seed/client-compat-v1.sql</c>, but that database has already been migrated by the
/// standing Honua Lambda. The seed therefore has to apply to the migrated shape, not only to the
/// fresh database <c>docker/client-compat</c> creates: on a migrated database the seed's own
/// <c>CREATE TABLE IF NOT EXISTS</c> is a no-op and every INSERT meets the columns the migrations
/// actually produce. The seed used to insert <c>honua.services.max_record_count</c>, a column the
/// migrations have never created, so the cert bootstrap died with
/// <c>42703 column "max_record_count" of relation "services" does not exist</c> and the lane could
/// never reach a serving assertion.
///
/// This test reproduces the bootstrap end to end against a real PostGIS container: the production
/// DbUp runner applies the real migration set, the seed is applied over it, and the fixture
/// contract the lane asserts (<c>test_service/0</c> serving exactly the ten client-compat-v1 names,
/// <c>test_service/10</c> accepting a run-owned add/delete) is verified through the FeatureServer
/// query path of a host bound to that database.
/// </summary>
[Collection("Database.CoreEndpoints")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class ClientCompatSeedMigratedDatabaseTests
{
    private const string PostgisImage = "postgis/postgis:16-3.4";
    private const string TestRunIdEnv = "HONUA_TEST_RUN_ID";
    private const string ServicePath = "/rest/services/test_service/FeatureServer/0";
    private const string ScratchPath = "/rest/services/test_service/FeatureServer/10";

    /// <summary>
    /// The ten names <c>scripts/cloud/lambda-certification.py</c> requires <c>test_service/0</c> to
    /// serve. Fixed here rather than read back from the fixture so a seed that silently loses or
    /// gains a record fails this test the same way it fails the certification lane.
    /// </summary>
    private static readonly string[] _expectedFixtureNames =
    [
        "alpha", "beta", "delta", "epsilon", "eta", "gamma", "iota", "lambda", "theta", "zeta"
    ];

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Operation(Operations.Create)]
    [Operation(Operations.Delete)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task ClientCompatSeed_OnServerMigratedDatabase_ServesTheTenFixtureNamesAndAcceptsScratchLayerWrites()
    {
        await using var container = new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithDatabase("honua_cert_seed_migrated")
            .WithUsername("postgres")
            .WithPassword("cert_seed_password")
            .WithEnvironment("POSTGIS_GDAL_ENABLED_DRIVERS", "ENABLE_ALL")
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable(TestRunIdEnv) ?? "manual")
            .Build();

        await container.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Timeout = 60,
            CommandTimeout = 180
        }.ToString();

        // The cert substrate enables PostGIS out of band (the honua-iac postgis-bootstrap Lambda
        // runs inside the VPC) because migration 001 creates GEOMETRY columns.
        await ExecuteAsync(
            connectionString,
            "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; " +
            "CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;");

        // Migrate first, exactly as the standing cert function does before the seed is applied.
        // This is the production runner and the production migration assembly — not a replay of
        // hand-picked scripts — so the shape the seed meets here is the shape the cert database has.
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);
        var migrations = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        migrations.Successful.Should().BeTrue(
            $"the certification database is migrated by the server before bootstrap. Error: {migrations.ErrorMessage}");

        var seedPath = RepositoryPaths.Resolve("tests", "seed", "client-compat-v1.sql");
        var seedSql = await File.ReadAllTextAsync(seedPath);

        // The bootstrap Lambda hands the seed to pg8000 as statements over the extended query
        // protocol; there is no psql to interpret backslash meta-commands. A seed that grew one
        // would apply locally and fail in the cert VPC, so pin that here.
        seedSql.Split('\n')
            .Where(line => line.TrimStart().StartsWith('\\'))
            .Should().BeEmpty("the cert bootstrap executes plain statements, not psql meta-commands");

        // Applied through the ADO.NET driver, which — like the bootstrap's pg8000 loop — parses the
        // file into individual statements and issues one Parse/Bind/Execute per statement.
        await ExecuteAsync(connectionString, seedSql);

        // The bootstrap is re-run whenever the standing cert database is re-primed, so the seed has
        // to stay applicable to the database it just produced, not only to the migrated one.
        await ExecuteAsync(connectionString, seedSql);

        await using var factory = ConfiguredWebApplicationFactory.Create(
            builder =>
            {
                // Migrations are already applied by the runner above; the host must read the
                // database as it finds it, exactly as the certified Lambda image does.
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(
                        WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                            connectionString,
                            new Dictionary<string, string?>
                            {
                                ["HONUA_ADMIN_PASSWORD"] = WebAppFixture.SharedAdminPassword
                            })));

                // FeatureServer edits are gated on the Pro entitlement
                // `editing.featureserver-edits`; an unlicensed host answers the lane's
                // test_service/10 write with HTTP 402 rather than an edit result. The standing
                // cert function carries a license, this in-process host does not, so grant the
                // same edition here. Nothing else about the seed contract depends on licensing.
                builder.ConfigureTestServices(services =>
                {
                    var license = new TestLicenseEntitlementService(HonuaEdition.Pro);
                    services.RemoveAll<ILicenseEntitlementService>();
                    services.RemoveAll<ILicenseStatusProvider>();
                    services.AddSingleton<ILicenseEntitlementService>(license);
                    services.AddSingleton<ILicenseStatusProvider>(license);
                });
            },
            "Test");

        // No dev-auth bypass. The lane's `invoke` helper defaults to `authenticated=True` and
        // sends the cert admin key on every serving call (scripts/cloud/lambda-certification.py),
        // so the fixture assertions run as that principal here too — an anonymous client is
        // refused on the scratch layer's writes and would not be exercising the lane's path.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);

        // --- test_service/0 serves exactly the ten client-compat-v1 records --------------------
        var countDocument = await GetJsonAsync(
            client,
            ServicePath + "/query?f=json&where=1%3D1&returnCountOnly=true");
        countDocument.RootElement.GetProperty("count").GetInt32()
            .Should().Be(10, "the certification lane requires exactly ten fixture rows on test_service/0");

        var namesDocument = await GetJsonAsync(
            client,
            ServicePath + "/query?f=json&where=1%3D1&outFields=name&returnGeometry=false");
        var servedNames = namesDocument.RootElement.GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        servedNames.Should().Equal(_expectedFixtureNames, "the fixture records must match client-compat-v1");

        // --- test_service/10 is the lane's run-owned scratch layer ------------------------------
        var marker = "honua-certseed-" + Guid.NewGuid().ToString("N");
        var markerQuery = ScratchPath + "/query?f=json&returnGeometry=false&outFields=*&where=" +
            Uri.EscapeDataString($"name = '{marker}'");

        var beforeCreate = await GetJsonAsync(client, markerQuery);
        beforeCreate.RootElement.GetProperty("features").GetArrayLength()
            .Should().Be(0, "the scratch layer must start free of this run's marker");

        var features = JsonSerializer.Serialize(new[]
        {
            new
            {
                attributes = new { name = marker },
                geometry = new { x = -122.42, y = 37.76, spatialReference = new { wkid = 4326 } }
            }
        });
        using var added = await PostFormAsync(client, ScratchPath + "/addFeatures", new Dictionary<string, string>
        {
            ["f"] = "json",
            ["features"] = features
        });
        var addResults = added.RootElement.GetProperty("addResults");
        addResults.GetArrayLength().Should().Be(1);
        addResults[0].GetProperty("success").GetBoolean()
            .Should().BeTrue("the scratch layer must accept the lane's run-owned insert");
        var objectId = addResults[0].GetProperty("objectId").GetInt64();

        var afterCreate = await GetJsonAsync(client, markerQuery);
        var created = afterCreate.RootElement.GetProperty("features");
        created.GetArrayLength().Should().Be(1, "the inserted row must be readable through the query path");
        created[0].GetProperty("attributes").GetProperty("name").GetString().Should().Be(marker);

        using var deleted = await PostFormAsync(client, ScratchPath + "/deleteFeatures", new Dictionary<string, string>
        {
            ["f"] = "json",
            ["objectIds"] = objectId.ToString(CultureInfo.InvariantCulture)
        });
        var deleteResults = deleted.RootElement.GetProperty("deleteResults");
        deleteResults.GetArrayLength().Should().Be(1);
        deleteResults[0].GetProperty("success").GetBoolean()
            .Should().BeTrue("the lane deletes the row it created");

        var afterDelete = await GetJsonAsync(client, markerQuery);
        afterDelete.RootElement.GetProperty("features").GetArrayLength()
            .Should().Be(0, "the deleted row must not still be served");

        // The scratch traffic must leave the certified fixture count untouched.
        var finalCount = await GetJsonAsync(
            client,
            ServicePath + "/query?f=json&where=1%3D1&returnCountOnly=true");
        finalCount.RootElement.GetProperty("count").GetInt32().Should().Be(10);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        return await ReadServingJsonAsync("GET", path, response);
    }

    private static async Task<JsonDocument> PostFormAsync(
        HttpClient client,
        string path,
        Dictionary<string, string> form)
    {
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(form));
        return await ReadServingJsonAsync("POST", path, response);
    }

    /// <summary>
    /// The GeoServices surface answers an operation failure with HTTP 200 and an <c>error</c>
    /// envelope, so a status-only assertion would read a refused edit as a pass. Fail on either,
    /// and carry the body into the message: a serving failure here has to name its own cause.
    /// </summary>
    private static async Task<JsonDocument> ReadServingJsonAsync(
        string method,
        string path,
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"{method} {path} returned {(int)response.StatusCode}: {Truncate(payload)}");
        var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            $"{method} {path} answered with a GeoServices error envelope: {Truncate(payload)}");
        return document;
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
}
