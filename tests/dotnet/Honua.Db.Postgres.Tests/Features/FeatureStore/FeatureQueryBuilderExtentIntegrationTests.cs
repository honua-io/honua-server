// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class FeatureQueryBuilderExtentIntegrationTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData(4326)]
    public async Task BuildExtentQuery_ProjectedOutput_TransformsRowsBeforeAggregation(int? sourceSrid)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ProjectedExtent");
        try
        {
            // Fixed UTM bounds provide an independent coordinate oracle. A bbox transform
            // instead of a per-row transform can add corners that were never in the data.
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (layer_id integer, geometry geometry(Point, 4326));
                INSERT INTO {schema}.features VALUES
                    (1, ST_Transform(ST_SetSRID(ST_MakePoint(361431.356, 3736037.969), 26911), 4326)),
                    (1, ST_Transform(ST_SetSRID(ST_MakePoint(483556.921, 3783589.624), 26911), 4326));
                """);
            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = builder.BuildExtentQuery(1, new FeatureQuery
            {
                OutputSrid = 26911,
                SpatialReferenceSrid = sourceSrid
            });
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            command.Parameters.AddWithValue(1);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetDouble(0).Should().BeApproximately(361431.356, 0.001);
            reader.GetDouble(1).Should().BeApproximately(3736037.969, 0.001);
            reader.GetDouble(2).Should().BeApproximately(483556.921, 0.001);
            reader.GetDouble(3).Should().BeApproximately(3783589.624, 0.001);
            reader.GetInt32(4).Should().Be(26911);
            reader.GetInt32(5).Should().Be(26911);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(null, null, 3857)]
    [InlineData(4326, null, 3857)]
    [InlineData(null, 4326, 4326)]
    [InlineData(3857, 4326, 4326)]
    public async Task GetExtent_NativeProjectedRows_ReportsObservedCrs(int? sourceHint, int? outputSrid, int expectedSrid)
    {
        var schema = await fixture.CreateIsolatedSchemaInternalAsync("ObservedExtent", applySeed: false);
        try
        {
            // Exact equatorial Web Mercator coordinates for longitude 1 and 2 degrees.
            // Null/empty rows contribute no bounds and a different layer must stay excluded.
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (layer_id integer, geometry geometry);
                INSERT INTO {{schema}}.features VALUES
                    (1, ST_SetSRID(ST_MakePoint(111319.49079327357, 0), 3857)),
                    (1, ST_SetSRID(ST_MakePoint(222638.98158654713, 0), 3857)),
                    (1, NULL), (1, ST_GeomFromText('POINT EMPTY', 4326)),
                    (2, ST_SetSRID(ST_MakePoint(-150, 20), 4326));
                """);
            var query = new FeatureQuery { SpatialReferenceSrid = sourceHint, OutputSrid = outputSrid };
            var extent = await ReadExtentAsync(schema, query);
            extent.Should().NotBeNull();
            var actual = extent ?? throw new InvalidOperationException("Expected nonempty extent.");
            actual.SpatialReference.Should().Be(expectedSrid);
            actual.MinX.Should().BeApproximately(outputSrid.HasValue ? 1 : 111319.49079327357, 1e-8);
            actual.MaxX.Should().BeApproximately(outputSrid.HasValue ? 2 : 222638.98158654713, 1e-8);
            actual.MinY.Should().BeApproximately(0, 1e-8);
            actual.MaxY.Should().BeApproximately(0, 1e-8);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetExtent_EnforcedAndRequestFilters_BindBoundsAndCrsToSameVisibleRows()
    {
        var schema = await fixture.CreateIsolatedSchemaInternalAsync("FilteredExtent", applySeed: false);
        try
        {
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (layer_id integer, geometry geometry, attributes jsonb);
                INSERT INTO {{schema}}.features VALUES
                    (1, ST_SetSRID(ST_MakePoint(1000, 2000), 3857), '{"owner":"allowed","category":"chosen"}'),
                    (1, ST_SetSRID(ST_MakePoint(-150, 20), 4326), '{"owner":"denied","category":"chosen"}'),
                    (1, ST_MakePoint(99, 88), '{"owner":"allowed","category":"excluded"}'),
                    (2, ST_MakePoint(77, 66), '{"owner":"allowed","category":"chosen"}');
                """);
            var query = new FeatureQuery
            {
                SqlFilter = new SqlFragment("attributes->>'category' = @p0", ["chosen"]),
                EnforcedSqlFilter = new SqlFragment("attributes->>'owner' = @p0", ["allowed"])
            };
            var extent = await ReadExtentAsync(schema, query);
            extent.Should().NotBeNull();
            var actual = extent ?? throw new InvalidOperationException("Expected visible extent.");
            actual.SpatialReference.Should().Be(3857);
            actual.MinX.Should().Be(1000);
            actual.MaxX.Should().Be(1000);
            actual.MinY.Should().Be(2000);
            actual.MaxY.Should().Be(2000);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(0, 0)]
    [InlineData(3857, 4326)]
    [InlineData(3857, 0)]
    public async Task GetExtent_UnknownOrMixedStoredCrs_RefusesInventedIdentity(int firstSrid, int secondSrid)
    {
        var schema = await fixture.CreateIsolatedSchemaInternalAsync("InvalidExtentCrs", applySeed: false);
        try
        {
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (layer_id integer, geometry geometry);
                INSERT INTO {{schema}}.features VALUES
                    (1, ST_SetSRID(ST_MakePoint(1, 2), {{firstSrid}})),
                    (1, ST_SetSRID(ST_MakePoint(3, 4), {{secondSrid}}));
                """);
            var action = () => ReadExtentAsync(schema, new FeatureQuery { SpatialReferenceSrid = 4326 });
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*one known coordinate reference system*");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetExtent_EmptyOrNullPopulation_RemainsNull(bool insertEmptyRows)
    {
        var schema = await fixture.CreateIsolatedSchemaInternalAsync("EmptyExtent", applySeed: false);
        try
        {
            await fixture.ExecuteAsync($"CREATE TABLE {schema}.features (layer_id integer, geometry geometry);");
            if (insertEmptyRows)
            {
                await fixture.ExecuteAsync($"INSERT INTO {schema}.features VALUES (1, NULL), (1, ST_GeomFromText('POINT EMPTY', 3857));");
            }
            (await ReadExtentAsync(schema, new FeatureQuery())).Should().BeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData(GeometryStorageType.Bytea, null, 3857)]
    [InlineData(GeometryStorageType.Bytea, 3857, 3857)]
    [InlineData(GeometryStorageType.Geography, null, 4326)]
    public async Task GetExtent_AlternativeStorage_PreservesObservedOrDeclaredCrs(
        GeometryStorageType storageType, int? sourceHint, int expectedSrid)
    {
        var schema = await fixture.CreateIsolatedSchemaInternalAsync("StoredExtent", applySeed: false);
        try
        {
            var columnType = storageType == GeometryStorageType.Bytea ? "bytea" : "geography";
            var geometry = storageType == GeometryStorageType.Bytea
                ? sourceHint.HasValue
                    ? "ST_AsBinary(ST_SetSRID(ST_MakePoint(1, 2), 3857))"
                    : "ST_AsEWKB(ST_SetSRID(ST_MakePoint(1, 2), 3857))"
                : "ST_SetSRID(ST_MakePoint(1, 2), 4326)::geography";
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (layer_id integer, geometry {{columnType}});
                INSERT INTO {{schema}}.features VALUES (1, {{geometry}});
                """);
            var extent = await ReadExtentAsync(schema, new FeatureQuery { SpatialReferenceSrid = sourceHint }, storageType);
            extent.Should().NotBeNull();
            var actual = extent ?? throw new InvalidOperationException("Expected stored extent.");
            actual.SpatialReference.Should().Be(expectedSrid);
            actual.MinX.Should().Be(1);
            actual.MaxY.Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private Task<FeatureExtent?> ReadExtentAsync(
        string schema, FeatureQuery query, GeometryStorageType storageType = GeometryStorageType.Geometry)
    {
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var pools = new DefaultObjectPoolProvider();
        var geometry = new GeometryProcessor();
        var builder = new FeatureQueryBuilder(pools.Create(new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy()), geometry, schema);
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            provider, geometry, Substitute.For<IFeatureCacheManager>(),
            pools.Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            null, NullLogger<FeatureDataAccess>.Instance, null, null, null, schema));
        return dataAccess.GetExtentAsync(1, builder.BuildExtentQuery(1, query, storageType), query, CancellationToken.None);
    }
}
