// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Transactions;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Caching;
using Honua.Db.Postgres.Features.Security;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Query)]
public sealed class PostgresBoundedSpatialPlannerIntegrationTests(DatabaseFixtureAdapter fixture) : IAsyncLifetime
{
    private readonly string _schema = "planner_read_" + Guid.NewGuid().ToString("N");
    private NpgsqlDataSource _source = null!;

    public async Task InitializeAsync()
    {
        await fixture.CreateSchemaUnderLockAsync(_schema);
        await fixture.ExecuteDdlUnderLockAsync($$"""
            CREATE TABLE {{_schema}}.points (id bigint PRIMARY KEY, geom geometry(Point,4326), label text, secret text);
            INSERT INTO {{_schema}}.points VALUES
                (1, ST_SetSRID(ST_MakePoint(0,0),4326), 'one', 'hidden-one'),
                (2, ST_SetSRID(ST_MakePoint(1,1),4326), 'two', 'hidden-two'),
                (3, ST_SetSRID(ST_MakePoint(50,50),4326), 'outside', 'hidden-three');
            CREATE VIEW {{_schema}}.observed AS
                SELECT *, current_setting('max_parallel_workers_per_gather') AS planner_setting
                FROM {{_schema}}.points;
            """);
        var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 4,
            MinPoolSize = 0,
            NoResetOnClose = true,
            Multiplexing = false,
            MaxAutoPrepare = 20,
            AutoPrepareMinUsages = 1
        }.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using var command = new NpgsqlCommand("SET max_parallel_workers_per_gather = 2", connection);
                command.ExecuteNonQuery();
            },
            async connection =>
            {
                await using var command = new NpgsqlCommand("SET max_parallel_workers_per_gather = 2", connection);
                await command.ExecuteNonQueryAsync();
            });
        _source = builder.Build();
    }

    public async Task DisposeAsync()
    {
        if (_source != null)
        {
            await _source.DisposeAsync();
        }

        await fixture.DropSchemaAsync(_schema);
    }

    [IntegrationTheory]
    [InlineData(false, "2")]
    [InlineData(true, "0")]
    public async Task QueryPage_EligibleBbox_AppliesOnlyOptedInPlannerSetting(bool enabled, string expected)
    {
        var result = await CreateReader(enabled).QueryPageAsync(1, Bbox());
        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        result.Items.Select(Setting).Should().OnlyContain(value => value == expected);
        await AssertPoolSettingAsync();
    }

    [IntegrationTheory]
    [InlineData(false, "2")]
    [InlineData(true, "0")]
    public async Task QueryPage_NormalizedPrimaryIdOrder_PreservesPlannerEligibility(bool enabled, string expected)
    {
        var resource = CreateResource(MetadataV2GeometryType.Point);
        var reader = CreateReader(enabled);
        var processor = new QueryProcessor(Substitute.For<IFilterExpressionTranslator>(), reader,
            NullLogger<QueryProcessor>.Instance);
        var normalized = processor.OptimizeQuery(new UnifiedQuery
        {
            Limit = 100,
            SpatialFilter = Bbox().SpatialFilter
        }, resource);
        var query = processor.ToFeatureQuery(normalized, resource);
        query.OrderBy.Should().NotBeNull();
        var ordering = query.OrderBy!.Value.Should().ContainSingle().Which;
        ordering.Field.Should().Be("id");
        ordering.Ascending.Should().BeTrue();
        ordering.NullOrdering.Should().Be(NullOrdering.Default);

        var result = await reader.QueryPageAsync(1, query);

        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        result.Items.Select(Setting).Should().OnlyContain(value => value == expected);
        await AssertPoolSettingAsync();
    }

    [IntegrationTheory]
    [InlineData(false, "2")]
    [InlineData(true, "0")]
    public async Task RegisteredFeatureStore_ForwardsPlannerProfileToBoundReader(bool enabled, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Limits:Connections:MaxConnectionPoolSize"] = "4",
            ["Limits:Connections:MinConnectionPoolSize"] = "0",
            ["Limits:Connections:MaxConcurrentQueries"] = "1",
            ["Limits:Connections:Multiplexing"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAdoNetDatabaseConnectionProvider>(new SourceProvider(_source));
        services.AddSingleton(_ => new SecureConnectionDataSourceCache(configuration));
        services.AddSingleton(_ => new QueryConcurrencyGate(PostgresDataSourceFactory.ResolveConnectionLimits(configuration)));
        services.AddScoped(registered => new CachingDatabaseConnectionProvider(
            _source, registered.GetRequiredService<ILogger<CachingDatabaseConnectionProvider>>(),
            concurrencyGate: registered.GetRequiredService<QueryConcurrencyGate>()));
        services.AddRefactoredFeatureStore(_schema, preferSerialBoundedSpatialReads: enabled);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFeatureDataProvider>();
        var resource = CreateResource(MetadataV2GeometryType.Point);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c max_parallel_workers_per_gather=2"
        }.ConnectionString;
        var binding = new FeatureProviderBinding(new MetadataV2Service(), resource, new MetadataV2Publication(),
            new MetadataV2StorageBinding { ResourceId = resource.Metadata.Id }, CreateMapping(sourceBacked: true),
            1, store, new DataConnection { Id = "planner-bound", IsEncrypted = false, ConnectionString = connectionString });
        var reader = (IPagedFeatureReader)((IBindableFeatureDataProvider)store).CreateReaderForBinding(binding);
        var result = await reader.QueryPageAsync(1, Bbox());
        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        result.Items.Select(Setting).Should().OnlyContain(value => value == expected);
        scope.ServiceProvider.GetRequiredService<QueryConcurrencyGate>().AvailableSlots.Should().Be(1);
        await using var connection = await scope.ServiceProvider.GetRequiredService<PostgresBoundConnectionProvider>()
            .OpenConnectionAsync("planner-bound", connectionString);
        await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", connection);
        (await command.ExecuteScalarAsync()).Should().Be("2", "the source pool must not retain the scoped setting");
    }

    [IntegrationTheory]
    [InlineData("larger-page")]
    [InlineData("later-page")]
    [InlineData("custom-order")]
    [InlineData("descending-id")]
    [InlineData("multiple-order-fields")]
    [InlineData("explicit-null-order")]
    [InlineData("distinct")]
    [InlineData("no-bbox")]
    [InlineData("unbounded")]
    [InlineData("include-null-geometry")]
    [InlineData("non-source-mapping")]
    [InlineData("unknown-geometry-type")]
    public async Task QueryPage_ExcludedShape_RetainsOrdinaryPlannerSetting(string shape)
    {
        var query = shape switch
        {
            "larger-page" => Bbox() with { Limit = 101 },
            "later-page" => Bbox() with { Offset = 1 },
            "custom-order" => Bbox() with { OrderBy = [new OrderByClause("label")] },
            "descending-id" => Bbox() with { OrderBy = [new OrderByClause("id", ascending: false)] },
            "multiple-order-fields" => Bbox() with { OrderBy = [new OrderByClause("id"), new OrderByClause("label")] },
            "explicit-null-order" => Bbox() with { OrderBy = [new OrderByClause("id") { NullOrdering = NullOrdering.NullsLast }] },
            "distinct" => Bbox() with { Distinct = true },
            "no-bbox" => Bbox() with { SpatialFilter = null },
            "unbounded" => Bbox() with { Limit = null },
            "include-null-geometry" => Bbox() with { IncludeNullGeometry = true },
            _ => Bbox()
        };
        var reader = CreateReader(sourceBacked: shape != "non-source-mapping",
            geometryType: shape == "unknown-geometry-type" ? MetadataV2GeometryType.None : MetadataV2GeometryType.Point);
        var result = await reader.QueryPageAsync(1, query);
        result.Items.Should().NotBeEmpty();
        result.Items.Select(Setting).Should().OnlyContain(value => value == "2");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task QueryPage_AmbientTransaction_RetainsOrdinaryPlannerSettingInsideAndAfterScope()
    {
        using (var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            var result = await CreateReader().QueryPageAsync(1, Bbox());
            result.Items.Select(Setting).Should().OnlyContain(value => value == "2");
            scope.Complete();
        }

        await AssertPoolSettingAsync();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPage_BorrowedMutationTransaction_RetainsSettingUntilOuterTransactionEnds(bool commit)
    {
        var provider = new SourceProvider(_source);
        var reader = CreateReader(provider: provider);
        var processId = await ReadPoolProcessIdAsync();
        await PostgresMutationTransaction.ExecuteAsync(provider, async () =>
        {
            Transaction.Current.Should().BeNull("the borrowed mutation scope does not use System.Transactions");
            await using var lease = await provider.OpenNpgsqlConnectionAsync();
            lease.Transaction.Should().NotBeNull();
            lease.Connection.ProcessID.Should().Be(processId);
            var result = await reader.QueryPageAsync(1, Bbox());
            result.Items.Select(Setting).Should().OnlyContain(value => value == "2");
            await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", lease);
            (await command.ExecuteScalarAsync()).Should().Be("2",
                "the read must leave the caller-owned transaction's planner setting unchanged");
            return true;
        }, _ => commit, CancellationToken.None);
        await AssertPoolSettingAsync(processId);
    }

    [IntegrationTest]
    public async Task QueryPage_ProjectedReprojectedSecuredPage_PreservesRowsGeometryMasksAndPaging()
    {
        var query = Bbox() with { Limit = 1, OutputSrid = 3857, OutFields = ["id", "label", "secret"] };
        var ordinary = await CreateReader(enabled: false, secured: true).QueryPageAsync(1, query);
        var scoped = await CreateReader(secured: true).QueryPageAsync(1, query);
        scoped.Items.Should().BeEquivalentTo(ordinary.Items, options => options.WithStrictOrdering());
        scoped.HasMoreResults.Should().Be(ordinary.HasMoreResults).And.BeFalse();
        scoped.Items.Should().ContainSingle().Which.Id.Should().Be(2);
        scoped.Items[0].Attributes.Should().NotContainKey("secret");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task QueryPage_FirstPageProbe_PreservesHasMoreAndRequestedPageSize()
    {
        var query = Bbox() with { Limit = 1 };
        var ordinary = await CreateReader(enabled: false).QueryPageAsync(1, query);
        var scoped = await CreateReader().QueryPageAsync(1, query);
        scoped.Items.Select(feature => feature.Id).Should().Equal(ordinary.Items.Select(feature => feature.Id));
        scoped.Items.Should().ContainSingle().Which.Id.Should().Be(1);
        scoped.HasMoreResults.Should().Be(ordinary.HasMoreResults).And.BeTrue();
        scoped.Items.Select(Setting).Should().OnlyContain(value => value == "0");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task CountStatisticsAndStreaming_OptedInReader_RetainOrdinaryPlannerSetting()
    {
        var reader = CreateReader();
        var query = Bbox() with { Where = "planner_setting = '2'" };
        (await reader.CountAsync(1, query)).Should().Be(2);
        var statistics = await reader.QueryStatisticsAsync(1, query with
        {
            GroupByFields = ["planner_setting"],
            OutStatistics = [new StatisticDefinition
            {
                StatisticType = StatisticType.Count, OnStatisticField = "id", OutStatisticFieldName = "total"
            }]
        });
        statistics.Should().ContainSingle();
        statistics[0]["planner_setting"].Should().Be("2");
        Convert.ToInt64(statistics[0]["total"], CultureInfo.InvariantCulture).Should().Be(2);
        var streamed = new List<Feature>();
        await foreach (var feature in reader.StreamFeaturesAsync(1, Bbox()))
        {
            streamed.Add(feature);
        }

        streamed.Should().HaveCount(2);
        streamed.Select(Setting).Should().OnlyContain(value => value == "2");
        await AssertPoolSettingAsync();
    }

    [IntegrationTest]
    public async Task QueryPage_SqlFailure_RestoresSettingOnSamePooledBackend()
    {
        var processId = await ReadPoolProcessIdAsync();
        var execute = () => CreateReader().QueryPageAsync(1, Bbox() with
        {
            SqlFilter = new SqlFragment("1 / @p0 = @p1", [0, 1])
        });
        (await execute.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22012");
        await AssertPoolSettingAsync(processId);
    }

    [IntegrationTest]
    public async Task QueryPage_Cancellation_RestoresSettingOnSamePooledBackend()
    {
        var processId = await ReadPoolProcessIdAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = CreateReader().QueryPageAsync(1, Bbox() with
        {
            SqlFilter = new SqlFragment("pg_sleep(@p0) IS NULL", [30d])
        }, cancellation.Token);
        try
        {
            await using var observer = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT wait_event FROM pg_stat_activity WHERE pid = $1", observer);
            command.Parameters.AddWithValue(processId);
            var elapsed = Stopwatch.StartNew();
            var sleeping = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (await command.ExecuteScalarAsync() is "PgSleep")
                {
                    sleeping = true;
                    break;
                }

                await Task.Delay(25);
            }

            sleeping.Should().BeTrue("cancellation must occur after the scoped feature SELECT starts");
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Observe cancellation before disposing the reader's connection;
                // the assertion below verifies the pending task was cancelled.
            }
        }

        var observe = async () => await pending;
        await observe.Should().ThrowAsync<OperationCanceledException>();
        await AssertPoolSettingAsync(processId);
    }

    [IntegrationTest]
    public async Task QueryPage_AutoPreparation_UsesSeparateOrdinaryAndScopedStatementIdentities()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var ordinary = await CreateReader(enabled: false).QueryPageAsync(1, Bbox());
            var scoped = await CreateReader().QueryPageAsync(1, Bbox());
            ordinary.Items.Select(Setting).Should().OnlyContain(value => value == "2");
            scoped.Items.Select(Setting).Should().OnlyContain(value => value == "0");
        }

        await using var connection = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT statement FROM pg_prepared_statements WHERE statement LIKE $1", connection);
        command.Parameters.AddWithValue($"%FROM \"{_schema}\".\"observed\"%");
        await using var reader = await command.ExecuteReaderAsync();
        var statements = new List<string>();
        while (await reader.ReadAsync())
        {
            statements.Add(reader.GetString(0));
        }

        statements.Should().HaveCount(2);
        statements.Should().ContainSingle(sql => sql.StartsWith("SELECT ALL ", StringComparison.Ordinal));
        statements.Should().ContainSingle(sql => sql.StartsWith("SELECT ", StringComparison.Ordinal) &&
            !sql.StartsWith("SELECT ALL ", StringComparison.Ordinal));
    }

    [IntegrationTest]
    public async Task ConcurrentMixedReads_AutoPreparation_DoesNotLeakPlannerSettingBetweenQueries()
    {
        var scopedReader = CreateReader();
        var ordinaryReader = CreateReader(enabled: false);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
        {
            if (index % 3 == 0)
            {
                (await scopedReader.CountAsync(1, Bbox() with { Where = "planner_setting = '2'" })).Should().Be(2);
            }
            else
            {
                var scoped = index % 3 == 1;
                var result = await (scoped ? scopedReader : ordinaryReader).QueryPageAsync(1, Bbox());
                result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
                result.Items.Select(Setting).Should().OnlyContain(value => value == (scoped ? "0" : "2"));
            }
        }));
        await AssertPoolSettingAsync();
    }

    private PostgresStorageMappedFeatureReader CreateReader(bool enabled = true, bool sourceBacked = true,
        MetadataV2GeometryType geometryType = MetadataV2GeometryType.Point, bool secured = false,
        IAdoNetDatabaseConnectionProvider? provider = null)
    {
        var resource = CreateResource(geometryType);
        var rls = Substitute.For<IRowLevelSecurityFilterSource>();
        rls.ResolveAsync(resource, Arg.Any<CancellationToken>()).Returns(new SqlFragment("\"id\" > @p0", [1]));
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(resource, Arg.Any<CancellationToken>()).Returns(["secret"]);
        return new PostgresStorageMappedFeatureReader(provider ?? new SourceProvider(_source),
            new DefaultObjectPoolProvider().Create(new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy()),
            resource,
            CreateMapping(sourceBacked),
            connection: null, connectionEncryptionService: null,
            rlsFilterSource: secured ? rls : null, fieldMaskSource: secured ? masks : null,
            preferSerialBoundedSpatialReads: enabled);
    }

    private static MetadataV2Resource CreateResource(MetadataV2GeometryType geometryType) =>
        new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "planner-probe", Name = "Planner probe" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial { GeometryType = geometryType },
            SchemaFields =
            [
                new() { Name = "id", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
                new() { Name = "geom", Type = MetadataV2FieldType.Geometry, SemanticRoles = ["geometry.primary"] },
                new() { Name = "label", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String },
                new() { Name = "planner_setting", Type = MetadataV2FieldType.String }
            ]
        };

    private FeatureStorageMapping CreateMapping(bool sourceBacked) =>
        new("observed", SchemaName: _schema, PrimaryKeyColumn: "id", GeometryColumn: "geom",
            StorageSrid: 4326, ProviderOptions: new Dictionary<string, string>
            {
                [FeatureStorageMapping.SourceBackedOption] = sourceBacked.ToString(CultureInfo.InvariantCulture)
            });

    private static FeatureQuery Bbox() => new()
    {
        Limit = 100,
        SpatialFilter = new SpatialFilter
        {
            Geometry = [],
            Srid = 4326,
            SpatialRelationship = SpatialRelationship.Intersects,
            IsSimpleEnvelope = true,
            EnvelopeMinX = -2,
            EnvelopeMinY = -2,
            EnvelopeMaxX = 2,
            EnvelopeMaxY = 2
        }
    };

    private static string? Setting(Feature feature) => feature.Attributes["planner_setting"]?.ToString();

    private async Task<int> ReadPoolProcessIdAsync()
    {
        await using var connection = await _source.OpenConnectionAsync();
        return connection.ProcessID;
    }

    private async Task AssertPoolSettingAsync(int? expectedProcessId = null)
    {
        await using var connection = await _source.OpenConnectionAsync();
        if (expectedProcessId.HasValue)
        {
            connection.ProcessID.Should().Be(expectedProcessId.Value);
        }

        await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", connection);
        (await command.ExecuteScalarAsync()).Should().Be("2");
    }

    private sealed class SourceProvider(NpgsqlDataSource source) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => source.ConnectionString;
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            await source.OpenConnectionAsync(cancellationToken);
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}
