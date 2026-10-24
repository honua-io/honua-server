// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using System.Reflection;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>Exercises the unqualified managed writer when the first search-path schema is empty.</summary>
[Collection("Database")]
public sealed class FeatureCacheSearchPathIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task AttributeOnlyUpdate_FeaturesInLaterSearchSchema_PreservesGeometryAndSrid()
    {
        var firstSchema = await fixture.CreateIsolatedSchemaInternalAsync("empty_search_path", applySeed: false);
        var featureSchema = await fixture.CreateIsolatedSchemaInternalAsync("managed_search_path", applySeed: false);
        // The production default has one cache entry for its fixed search path. Preserve it
        // around this isolated database scenario; do not let another fixture's cached type mask it.
        var cache = (ConcurrentDictionary<string, int>)typeof(FeatureCacheManager)
            .GetField("_geometryStorageTypeCache", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        var hadCached = cache.TryRemove(string.Empty, out var cached);
        try
        {
            await fixture.ExecuteAsync($$"""
                CREATE TABLE "{{featureSchema}}".features (
                    objectid bigint PRIMARY KEY, layer_id integer NOT NULL,
                    geometry geometry, attributes jsonb);
                CREATE INDEX features_transform_probe ON "{{featureSchema}}".features
                    USING gist ((ST_Transform(geometry, 4326)::geography));
                INSERT INTO "{{featureSchema}}".features VALUES
                    (1, 2147483646, ST_SetSRID(ST_MakePoint(-156.4, 20.8), 4326), '{"note":"before"}');
                """);
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = $"{firstSchema},{featureSchema},public"
            }.ConnectionString;
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
            provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
                (DbConnection)await dataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
            var manager = new FeatureCacheManager(provider, NullLogger<FeatureCacheManager>.Instance);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var before = new NpgsqlCommand("SELECT current_schema(), ST_AsBinary(geometry) FROM features WHERE objectid=1", connection);
            byte[] originalWkb;
            await using (var reader = await before.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be(firstSchema);
                originalWkb = reader.GetFieldValue<byte[]>(1);
            }
            (await manager.GetGeometryStorageTypeAsync(CancellationToken.None))
                .Should().Be(GeometryStorageType.Geometry, "features resolves beyond current_schema()");
            var writer = new FeatureDataAccess(new FeatureDataAccessDependencies(
                provider, new GeometryProcessor(), manager,
                new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
                null, NullLogger<FeatureDataAccess>.Instance, null, null, null, null));
            // Attribute-only protocol edits reuse ST_AsBinary, which intentionally omits SRID.
            var updated = await writer.UpdateFeatureAsync(2147483646,
                Feature.Create(1, originalWkb, ImmutableDictionary<string, object?>.Empty.Add("note", "after")),
                CancellationToken.None);
            updated.Attributes["note"]!.ToString().Should().Be("after");
            await using var after = new NpgsqlCommand(
                "SELECT ST_SRID(geometry), ST_AsBinary(geometry), attributes->>'note' FROM features WHERE objectid=1", connection);
            await using var readback = await after.ExecuteReaderAsync();
            (await readback.ReadAsync()).Should().BeTrue();
            readback.GetInt32(0).Should().Be(4326);
            readback.GetFieldValue<byte[]>(1).Should().Equal(originalWkb);
            readback.GetString(2).Should().Be("after");
        }
        finally
        {
            cache.TryRemove(string.Empty, out _);
            if (hadCached)
            {
                cache[string.Empty] = cached;
            }
            await fixture.DropSchemaAsync(featureSchema);
            await fixture.DropSchemaAsync(firstSchema);
        }
    }
}
