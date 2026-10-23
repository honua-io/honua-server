// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresPagedProjectionIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresPagedProjectionIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.points (id bigint PRIMARY KEY, geom geometry(Point,4326),
                label text, attributes integer, tenant integer);
            INSERT INTO {{_schema}}.points
                SELECT i, ST_SetSRID(ST_MakePoint(i / 100.0, 1),4326), 'point-' || i, 1001-i, i % 2
                FROM generate_series(1,1000) i;
            ANALYZE {{_schema}}.points;
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTest]
    public async Task DeepPage_FormatsOnlyReturnedRows_AndPreservesProbeAndGeometry()
    {
        var reader = CreateReader();
        var query = new FeatureQuery { Offset = 900, Limit = 10 };
        var page = await reader.QueryPageAsync(1, query);
        page.Items.Select(feature => feature.Id).Should().Equal(Enumerable.Range(901, 10).Select(i => (long)i));
        page.HasMoreResults.Should().BeTrue();
        page.Items.Should().OnlyContain(feature => feature.Geometry != null && feature.Attributes["label"] != null);

        // Inspect executed plan work rather than asserting elapsed time: the previous
        // query formatted all 911 scanned rows, although only 11 can leave pagination.
        var sql = typeof(PostgresStorageMappedFeatureReader)
            .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reader, [query, true])!;
        var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, VERBOSE, FORMAT JSON) " + sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter!);
        }
        using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var projections = ProjectionRows(plan.RootElement[0].GetProperty("Plan")).ToArray();
        projections.Should().NotBeEmpty();
        projections.Should().OnlyContain(rows => rows <= 11,
            "geometry and JSON encoding must run after OFFSET and LIMIT, including the one-row has-more probe");
    }

    [IntegrationTest]
    public async Task Page_SortsPhysicalAttributeNamedLikeOutputAlias_AndFiltersBeforeOffset()
    {
        var page = await CreateReader().QueryPageAsync(1, new FeatureQuery
        {
            Where = "tenant = 1",
            Offset = 3,
            Limit = 2,
            OrderBy = [new OrderByClause("attributes")],
            OutFields = ["label"]
        });
        page.Items.Select(feature => feature.Id).Should().Equal(993L, 991L);
        page.Items.Should().OnlyContain(feature => !feature.Attributes.ContainsKey("tenant"));
        page.HasMoreResults.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Page_DoesNotRequireSelectPrivilegesOnUnrequestedColumns()
    {
        var role = "projection_reader_" + Guid.NewGuid().ToString("N");
        await fixture.ExecuteAsync($$"""
            CREATE ROLE {{role}};
            GRANT USAGE ON SCHEMA {{_schema}} TO {{role}};
            GRANT SELECT (id,geom,label) ON {{_schema}}.points TO {{role}};
            """);
        try
        {
            var sql = typeof(PostgresStorageMappedFeatureReader)
                .GetMethod("BuildFeatureSelect", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(CreateReader(), [new FeatureQuery { Offset = 5, Limit = 2, OutFields = ["label"] }, false])!;
            var parameters = (IReadOnlyList<object?>)sql.GetType().GetProperty("Parameters")!.GetValue(sql)!;
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var selectRole = new NpgsqlCommand($"SET LOCAL ROLE {role}", connection, transaction))
            {
                await selectRole.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand(sql.ToString(), connection, transaction);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter!);
            }
            await using var results = await command.ExecuteReaderAsync();
            (await results.ReadAsync()).Should().BeTrue();
            results.GetInt64(0).Should().Be(6);
        }
        finally
        {
            await fixture.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role};");
        }
    }

    [IntegrationTest]
    public async Task Page_PreservesNullOrderingAndMaskedFields()
    {
        await fixture.ExecuteAsync($"UPDATE {_schema}.points SET attributes = NULL WHERE id = 999");
        var page = await CreateReader().QueryPageAsync(1, new FeatureQuery
        {
            Where = "id >= 995",
            Offset = 1,
            Limit = 2,
            OrderBy = [new OrderByClause("attributes") { NullOrdering = NullOrdering.NullsFirst }],
            EnforcedMaskedFields = ["tenant"]
        });
        page.Items.Select(feature => feature.Id).Should().Equal(1000L, 998L);
        page.Items.Should().OnlyContain(feature => !feature.Attributes.ContainsKey("tenant"));
    }

    [IntegrationTest]
    public async Task NearestPage_PreservesDistanceOrderCountAndOutputProjection()
    {
        var origin = new WKBWriter().Write(new Point(0, 1));
        var page = await CreateReader().QueryPageAsync(1, new FeatureQuery
        {
            Offset = 1,
            OutputSrid = 3857,
            SpatialFilter = SpatialFilter.CreateKnnFilter(origin, count: 3, returnDistance: true, srid: 4326)
        });
        page.Items.Select(feature => feature.Id).Should().Equal(2L, 3L, 4L);
        page.HasMoreResults.Should().BeFalse();
        page.Items.Select(feature => Convert.ToDouble(feature.Attributes["distance"], System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeInAscendingOrder().And.OnlyContain(distance => distance > 2000);
        new WKBReader().Read(page.Items[0].Geometry!).Coordinate.X.Should().BeApproximately(2226.3898, 0.01);
    }

    [IntegrationTest]
    public async Task Stream_OffsetOnlyAndExcludedAttributes_PreservesLastRows()
    {
        var ids = new List<long>();
        await foreach (var feature in CreateReader().StreamFeaturesAsync(1,
                           new FeatureQuery { Offset = 997, ExcludeAttributes = true }))
        {
            ids.Add(feature.Id);
            feature.Attributes.Should().NotContainKey("label");
        }
        ids.Should().Equal(998L, 999L, 1000L);
    }

    [IntegrationTest]
    public async Task Page_JsonAttributes_PreservesNumericSortTypesMasksAndAliasCollisions()
    {
        await fixture.ExecuteAsync($$"""
            ALTER TABLE {{_schema}}.points ADD COLUMN __honua_page_order_0 jsonb;
            UPDATE {{_schema}}.points SET __honua_page_order_0 = jsonb_build_object(
                'id',id,'label',label,'attributes',attributes,'tenant',tenant);
            """);
        var page = await CreateReader(attributesColumn: "__honua_page_order_0").QueryPageAsync(1, new FeatureQuery
        {
            Offset = 8,
            Limit = 3,
            OrderBy = [new OrderByClause("attributes")],
            OutFields = ["label", "attributes", "tenant"],
            EnforcedMaskedFields = ["tenant"]
        });
        page.Items.Select(feature => feature.Id).Should().Equal(992L, 991L, 990L);
        page.Items.Select(feature => Convert.ToInt32(feature.Attributes["attributes"], System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal(9, 10, 11);
        page.Items.Should().OnlyContain(feature => !(feature.Attributes["attributes"] is string) &&
            !feature.Attributes.ContainsKey("tenant"));
    }

    private static IEnumerable<double> ProjectionRows(JsonElement node)
    {
        if (node.TryGetProperty("Output", out var output) &&
            output.EnumerateArray().Any(value =>
                value.GetString()!.Contains("jsonb_build_object", StringComparison.Ordinal) ||
                value.GetString()!.Contains("st_asbinary", StringComparison.Ordinal)))
        {
            yield return node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble();
        }
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var rows in ProjectionRows(child))
                {
                    yield return rows;
                }
            }
        }
    }

    private PostgresStorageMappedFeatureReader CreateReader(string? attributesColumn = null)
    {
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Name = "projection-points" },
            SchemaFields =
            [
                new() { Name = "id", Type = MetadataV2FieldType.BigInteger },
                new() { Name = "label", Type = MetadataV2FieldType.String },
                new() { Name = "attributes", Type = MetadataV2FieldType.Integer },
                new() { Name = "tenant", Type = MetadataV2FieldType.Integer }
            ]
        };
        return new PostgresStorageMappedFeatureReader(provider,
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            resource, new FeatureStorageMapping("points", SchemaName: _schema, PrimaryKeyColumn: "id", GeometryColumn: "geom",
                AttributesColumn: attributesColumn),
            connection: null, connectionEncryptionService: null);
    }
}
