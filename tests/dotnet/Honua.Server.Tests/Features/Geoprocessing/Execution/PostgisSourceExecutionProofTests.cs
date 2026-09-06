// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Collection("Database")]
[Trait("Category", "RemoteSourceExecutionProof")]
public sealed class PostgisSourceExecutionProofTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    [Fact]
    public async Task Postgis_RegisteredExternalDatabase_PublishesOnlyPredicateWatermarkAndBboxMatches()
    {
        const string password = "proof-secret-3950";
        await using var external = new PostgreSqlBuilder().WithImage("postgis/postgis:17-3.5")
            .WithDatabase("external_survey").WithUsername("postgres").WithPassword(password).Build();
        await external.StartAsync();
        var connectionString = external.GetConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                CREATE EXTENSION IF NOT EXISTS postgis;
                CREATE TABLE survey (key integer PRIMARY KEY, name text, active boolean, updated_at timestamptz,
                    reading numeric, geom geometry(PointZ,4326));
                INSERT INTO survey VALUES
                  (11,'Kīlauea 日本',true,'2026-01-02T00:00:00Z',12.5,ST_GeomFromEWKT('SRID=4326;POINT Z (1 2 3.25)')),
                  (12,NULL,true,'2026-01-03T00:00:00Z',NULL,ST_GeomFromEWKT('SRID=4326;POINT Z (3 4 -1.5)')),
                  (13,'last',true,'2026-01-04T00:00:00Z',0,ST_GeomFromEWKT('SRID=4326;POINT Z (5 6 9.75)')),
                  (21,'inactive',false,'2026-01-03T00:00:00Z',99,ST_GeomFromEWKT('SRID=4326;POINT Z (1 2 3)')),
                  (22,'too old',true,'2026-01-01T23:59:59Z',99,ST_GeomFromEWKT('SRID=4326;POINT Z (1 2 3)')),
                  (23,'outside',true,'2026-01-03T00:00:00Z',99,ST_GeomFromEWKT('SRID=4326;POINT Z (50 60 3)')),
                  (24,'null geometry',true,'2026-01-03T00:00:00Z',99,NULL);
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        // Register encrypted credentials in the actual Honua catalog (the first DB).
        // The source table exists only in the second DB; catalog fallback cannot pass.
        var encryption = fixture.GetService<IConnectionEncryptionService>();
        var registry = fixture.GetService<ISecureConnectionRegistry>();
        var registered = await registry.CreateConnectionAsync(DataConnection.CreateWithEncryptedCredentials(
            "external-proof-" + Guid.NewGuid().ToString("N"), builder.Host!, builder.Port, builder.Database!, builder.Username!,
            await encryption.EncryptConnectionStringAsync(connectionString), await encryption.GetCurrentKeyVersionAsync(),
            "proof", sslRequired: false, sslMode: Honua.Core.Features.Security.Domain.SslMode.Disable));
        try
        {
            var persisted = await registry.GetConnectionByNameAsync(registered.Name);
            persisted.Should().NotBeNull();
            persisted!.EncryptedConnectionString.Should().NotBeNullOrEmpty();
            var realResolver = fixture.GetService<ISecureConnectionResolver>();
            // Observe resolution while delegating to the real registry/decryption path.
            var observed = Substitute.For<ISecureConnectionResolver>();
            observed.ResolveConnectionStringAsync(registered.Name, Arg.Any<CancellationToken>())
                .Returns(call => realResolver.ResolveConnectionStringAsync(registered.Name, call.ArgAt<CancellationToken>(1)));
            var services = new ServiceCollection();
            services.AddSingleton(observed);
            services.AddSingleton<IDagFeatureSource>(new ExternalPostgisDagSource());
            using var provider = services.BuildServiceProvider();
            using var output = await RemoteSourceProof.Execute(provider, "source.postgis",
                ("connectionName", registered.Name), ("table", "survey"), ("where", "active = true"),
                ("since", "2026-01-02T00:00:00Z"), ("watermarkField", "updated_at"), ("bbox", "0,0,10,10"), ("outSrid", "4326"));
            await observed.Received(1).ResolveConnectionStringAsync(registered.Name, Arg.Any<CancellationToken>());
            output.RootElement.GetRawText().Should().NotContain(password).And.NotContain(connectionString);
            var features = output.RootElement.GetProperty("features").EnumerateArray()
                .OrderBy(f => f.GetProperty("properties").GetProperty("key").GetInt32()).ToArray();
            features.Should().HaveCount(3);
            double[][] expected = [[1, 2, 3.25], [3, 4, -1.5], [5, 6, 9.75]];
            string?[] names = ["Kīlauea 日本", null, "last"];
            for (var i = 0; i < 3; i++)
            {
                var properties = features[i].GetProperty("properties");
                properties.GetProperty("key").GetInt32().Should().Be(11 + i);
                properties.GetProperty("name").GetString().Should().Be(names[i]);
                properties.GetProperty("active").GetBoolean().Should().BeTrue();
                properties.TryGetProperty("geom", out _).Should().BeFalse("geometry is not a scalar attribute");
                properties.GetProperty("updated_at").GetDateTimeOffset().Should()
                    .Be(new DateTimeOffset(2026, 1, 2 + i, 0, 0, 0, TimeSpan.Zero));
                features[i].GetProperty("geometry").GetProperty("type").GetString().Should().Be("Point");
                features[i].GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(v => v.GetDouble())
                    .Should().Equal(expected[i]);
            }
            features[0].GetProperty("properties").GetProperty("reading").GetDecimal().Should().Be(12.5m);
            features[1].GetProperty("properties").GetProperty("reading").ValueKind.Should().Be(JsonValueKind.Null);
            features[2].GetProperty("properties").GetProperty("reading").GetDecimal().Should().Be(0m);
            // Canonical GeoJSON without an explicit alternate CRS is longitude/latitude WGS84.
            var decoded = new NetTopologySuite.IO.GeoJsonReader().Read<NetTopologySuite.Features.FeatureCollection>(output.RootElement.GetRawText());
            decoded.Select(f => f.Geometry.SRID).Should().OnlyContain(srid => srid == 4326);
        }
        finally
        {
            await registry.DeleteConnectionAsync(registered.ConnectionId);
        }
    }
}
