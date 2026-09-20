// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>Exercises managed branch overlays through the production mapped reader (#5044).</summary>
[Collection("Database")]
public sealed class PostgresStorageMappedFeatureReaderVersionIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly Guid _version = Guid.NewGuid();
    private readonly Guid _otherVersion = Guid.NewGuid();
    private string _schema = null!;
    private FeatureQuery Branch => new() { VersionContext = new VersionContext { VersionId = _version } };

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStorageMappedFeatureReaderVersionIntegrationTests));
        // Use the owning production table/index DDL. Change tracking triggers are
        // unrelated to this read regression and require the replication fixture.
        foreach (var filename in new[] { "047_CreateGdbVersions.sql", "049_CreateVersionEdits.sql" })
        {
            var assembly = typeof(Program).Assembly;
            var name = assembly.GetManifestResourceNames().Single(item => item.EndsWith(filename, StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var ddl = await reader.ReadToEndAsync();
            var triggerStart = ddl.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
            await fixture.ExecuteAsync(triggerStart < 0 ? ddl : ddl[..triggerStart]);
        }

        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.features (
                layer_id integer NOT NULL, objectid bigint NOT NULL,
                geometry geometry(Point,4326), attributes jsonb,
                PRIMARY KEY (layer_id,objectid));
            INSERT INTO {{_schema}}.features VALUES
                (1,1,ST_SetSRID(ST_MakePoint(1,1),4326),'{"count":1,"tenant":"a","secret":"base-one"}'),
                (1,2,ST_SetSRID(ST_MakePoint(2,2),4326),'{"count":2,"tenant":"a","secret":"base-two"}'),
                (1,3,ST_SetSRID(ST_MakePoint(3,3),4326),'{"count":3,"tenant":"b","secret":"base-three"}'),
                (2,1,ST_SetSRID(ST_MakePoint(90,90),4326),'{"count":900,"tenant":"a"}');
            INSERT INTO honua.gdb_versions (version_id,version_name,owner) VALUES
                ('{{_version}}','{{_version}}','mapped-reader-test'),
                ('{{_otherVersion}}','{{_otherVersion}}','mapped-reader-test');
            INSERT INTO honua.version_edits (version_id,layer_id,objectid,operation,geometry,attributes) VALUES
                ('{{_version}}',1,1,2,ST_SetSRID(ST_MakePoint(10,10),4326),'{"count":100,"tenant":"a","secret":"branch-one"}'),
                ('{{_version}}',1,2,3,NULL,NULL),
                ('{{_version}}',1,4,1,ST_SetSRID(ST_MakePoint(4,4),4326),'{"count":4,"tenant":"a","secret":"branch-four"}'),
                ('{{_version}}',2,3,3,NULL,NULL),
                ('{{_otherVersion}}',1,3,3,NULL,NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        await fixture.ExecuteAsync($"DELETE FROM honua.gdb_versions WHERE version_id IN ('{_version}','{_otherVersion}');");
        await fixture.DropSchemaAsync(_schema);
    }

    [Fact]
    public async Task Query_OverlaysUpdateInsertDeleteWithoutChangingDefaultOrOtherLayers()
    {
        var reader = CreateReader();
        var branch = await reader.QueryAsync(1, Branch with { OrderBy = [new OrderByClause("objectid")] });
        branch.Items.Select(item => item.Id).Should().Equal(1L, 3L, 4L);
        branch.Items.Select(item => Convert.ToInt64(item.Attributes["count"], CultureInfo.InvariantCulture))
            .Should().Equal(100L, 3L, 4L);
        var baseline = await reader.QueryAsync(1, new FeatureQuery { OrderBy = [new OrderByClause("objectid")] });
        baseline.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L);
        baseline.Items.Select(item => Convert.ToInt64(item.Attributes["count"], CultureInfo.InvariantCulture))
            .Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task CountIdsAndPages_ApplyFiltersToEffectiveBranchRows()
    {
        var reader = CreateReader();
        (await reader.CountAsync(1, Branch with { Where = "count >= 4" })).Should().Be(2);
        (await reader.QueryObjectIdsAsync(1, Branch with
        {
            Where = "count >= 4",
            OrderBy = [new OrderByClause("objectid")]
        })).Should().Equal(1L, 4L);
        var page = await reader.QueryPageAsync(1, Branch with
        {
            OrderBy = [new OrderByClause("objectid")],
            Offset = 1,
            Limit = 1
        });
        page.Items.Select(item => item.Id).Should().Equal(3L);
        page.HasMoreResults.Should().BeTrue();
        (await reader.QueryObjectIdsAsync(1, Branch with { ObjectIds = [2, 4] })).Should().Equal(4L);
    }

    [Fact]
    public async Task ExtentAndStatistics_UseBranchGeometryAndAttributeImages()
    {
        var reader = CreateReader();
        var extent = await reader.GetExtentAsync(1, Branch);
        extent.Should().NotBeNull();
        extent!.Value.MinX.Should().Be(3);
        extent.Value.MaxX.Should().Be(10);
        var statistics = await reader.QueryStatisticsAsync(1, Branch with
        {
            GroupByFields = ["tenant"],
            OrderBy = [new OrderByClause("tenant")],
            OutStatistics = [new StatisticDefinition
            {
                StatisticType = StatisticType.Sum, OnStatisticField = "count", OutStatisticFieldName = "total"
            }]
        });
        statistics.Select(row => row["tenant"]).Should().Equal("a", "b");
        statistics.Select(row => Convert.ToInt64(row["total"], CultureInfo.InvariantCulture)).Should().Equal(104L, 3L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericWhere_UsesDeclaredJsonTypeOnDefaultAndBranch(bool branch)
    {
        await fixture.ExecuteAsync($$"""
            INSERT INTO {{_schema}}.features (layer_id,objectid,attributes) VALUES
                (1,5,'{"count":"","tenant":"a"}'),(1,6,'{"count":null,"tenant":"a"}');
            """);
        var reader = CreateReader();
        var query = branch ? Branch : new FeatureQuery();
        (await reader.QueryObjectIdsAsync(1, query with { Where = "count >= 2" }))
            .Should().Equal(branch ? new long[] { 1, 3, 4 } : [2, 3]);
        (await reader.QueryObjectIdsAsync(1, query with { Where = "count IN (1,4,100)" }))
            .Should().Equal(branch ? new long[] { 1, 4 } : [1]);
        (await reader.QueryObjectIdsAsync(1, query with { Where = "count = '100'" }))
            .Should().Equal(branch ? new long[] { 1 } : []);
        (await reader.QueryObjectIdsAsync(1, query with { Where = "count IS NULL" })).Should().Equal(5L, 6L);
        (await reader.QueryObjectIdsAsync(1, query with { Where = "tenant = 'a' AND count LIKE '1%'" }))
            .Should().Equal(1L);
    }

    [Fact]
    public async Task EncodedAndTileQueries_DoNotResurrectDeletedBranchRows()
    {
        var reader = CreateReader();
        var deleted = Branch with { ObjectIds = [2] };
        (await reader.QueryFlatGeobufAsync(1, deleted)).Should().BeNull();
        (await reader.QueryGeobufAsync(1, deleted)).Should().BeNull();
        (await reader.QueryFlatGeobufAsync(1, Branch with { ObjectIds = [4] })).Should().NotBeNullOrEmpty();
        (await reader.QueryGeobufAsync(1, Branch with { ObjectIds = [4] })).Should().NotBeNullOrEmpty();
        var tile = await reader.GetMvtTileAsync(1, 0, 0, 0, deleted,
            new TileOptions(), new TileLimits());
        tile.Should().BeNullOrEmpty();
        var inserted = await reader.GetMvtTileAsync(1, 0, 0, 0, Branch with { ObjectIds = [4] },
            new TileOptions(), new TileLimits());
        inserted.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Security_AppliesPermanentFilterRlsAndMasksToBothSources(bool branch)
    {
        var reader = CreateReader(secured: true);
        var query = branch ? Branch : new FeatureQuery();
        var result = await reader.QueryAsync(1, query);
        result.Items.Select(item => item.Id).Should().Equal(branch ? new long[] { 4 } : [1, 2]);
        result.Items.Should().OnlyContain(item => !item.Attributes.ContainsKey("secret"));
        (await reader.CountAsync(1, query)).Should().Be(branch ? 1 : 2);
        (await reader.QueryObjectIdsAsync(1, query)).Should().Equal(branch ? new long[] { 4 } : [1, 2]);
        var act = () => reader.QueryAsync(1, query with { Where = "secret = 'branch-four'" });
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExternalTableMapping_RejectsBranchInsteadOfReturningDefault()
    {
        var reader = CreateReader(externalTable: true);
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.QueryAsync(1, Branch));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.CountAsync(1, Branch));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.QueryObjectIdsAsync(1, Branch));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.QueryFlatGeobufAsync(1, Branch));
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.QueryGeobufAsync(1, Branch));
    }

    [Fact]
    public async Task ExternalConnection_RejectsBranchBeforeOpeningTheOtherDatabase()
    {
        var external = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "not_the_managed_database" };
        var reader = CreateReader(connection: new DataConnection
        {
            Id = "external-version-test",
            ConnectionString = external.ConnectionString,
            IsEncrypted = false
        });
        var act = () => reader.QueryAsync(1, Branch);
        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("external database connection");
    }

    [Fact]
    public void ExplicitDefault_LeavesSqlAndParameterOrderingUnchanged()
    {
        var reader = CreateReader();
        var method = typeof(PostgresStorageMappedFeatureReader).GetMethod("BuildFeatureSelect", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var query = new FeatureQuery { Where = "count >= 2", OrderBy = [new OrderByClause("objectid")], Limit = 2 };
        var implicitSql = method.Invoke(reader, [query, false])!;
        var explicitSql = method.Invoke(reader, [query with { VersionContext = VersionContext.Default }, false])!;
        explicitSql.ToString().Should().Be(implicitSql.ToString()).And.NotContain("version_edits");
        var parameters = implicitSql.GetType().GetProperty("Parameters")!;
        ((IEnumerable<object?>)parameters.GetValue(explicitSql)!).Should()
            .Equal((IEnumerable<object?>)parameters.GetValue(implicitSql)!);
    }

    private PostgresStorageMappedFeatureReader CreateReader(bool secured = false, bool externalTable = false, DataConnection? connection = null)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "branch-test", Name = "Branch test" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new() { Name = "geometry", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] },
                new() { Name = "count", Type = MetadataV2FieldType.Integer },
                new() { Name = "tenant", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String }
            ],
            PermanentFilter = secured ? new MetadataV2PermanentFilter { Expression = "count < 50" } : null
        };
        var filters = Substitute.For<IFilterExpressionService>();
        var expression = new Literal(true, LiteralType.Boolean);
        filters.ParseAndNormalize(FilterLanguage.ArcGisSql, "count < 50", resource)
            .Returns(FilterParseResult.Success(expression));
        filters.Translate(expression, resource).Returns(FilterTranslationResult.Success(expression,
            new SqlFragment("(\"attributes\"->>'count')::integer < @p0", [50])));
        var rls = Substitute.For<IRowLevelSecurityFilterSource>();
        rls.ResolveAsync(resource, Arg.Any<CancellationToken>()).Returns(
            new SqlFragment("\"attributes\"->>'tenant' = @p0", ["a"]));
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(resource, Arg.Any<CancellationToken>()).Returns(["secret"]);
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
        return new PostgresStorageMappedFeatureReader(new FixtureConnectionProvider(fixture.ConnectionString),
            pool, resource,
            new FeatureStorageMapping(externalTable ? "external_features" : "features", SchemaName: _schema,
                GeometryColumn: "geometry", StorageSrid: 4326, AttributesColumn: "attributes",
                LayerDiscriminatorColumn: "layer_id", LayerDiscriminatorValue: 1,
                ProviderOptions: new Dictionary<string, string> { [FeatureStorageMapping.SourceBackedOption] = "true" }),
            connection: connection, connectionEncryptionService: null,
            filterExpressionService: secured ? filters : null,
            rlsFilterSource: secured ? rls : null, fieldMaskSource: secured ? masks : null);
    }

    private sealed class FixtureConnectionProvider(string connectionString) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => connectionString;
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead, CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
