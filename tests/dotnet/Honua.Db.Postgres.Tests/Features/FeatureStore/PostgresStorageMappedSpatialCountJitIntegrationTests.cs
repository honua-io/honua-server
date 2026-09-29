// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Transactions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.IO;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresStorageMappedSpatialCountJitIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;
    private NpgsqlDataSource _source = null!;
    private int _backendPid;
    private readonly IAdoNetDatabaseConnectionProvider _provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresStorageMappedSpatialCountJitIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.points (id bigint PRIMARY KEY, geom geometry(Point, 4326));
            INSERT INTO {{_schema}}.points VALUES
                (1, ST_SetSRID(ST_MakePoint(-1, -1), 4326)),
                (2, ST_SetSRID(ST_MakePoint(0, 0), 4326)),
                (3, ST_SetSRID(ST_MakePoint(1, 1), 4326)),
                (4, ST_SetSRID(ST_MakePoint(2, 2), 4326)),
                (5, NULL), (6, ST_GeomFromText('POINT EMPTY', 4326));
            CREATE TABLE {{_schema}}.observations (jit text, workers text);
            CREATE FUNCTION {{_schema}}.observe_geom(value geometry) RETURNS geometry
            LANGUAGE plpgsql VOLATILE AS $body$
            BEGIN
                INSERT INTO {{_schema}}.observations VALUES
                    (current_setting('jit'), current_setting('max_parallel_workers_per_gather'));
                RETURN value;
            END $body$;
            CREATE VIEW {{_schema}}.read_points AS SELECT id, {{_schema}}.observe_geom(geom) AS geom FROM {{_schema}}.points;
            """);

        // Deliberately disable pool reset: successful checks must reflect batch cleanup,
        // rather than Npgsql silently restoring settings on the next checkout.
        var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            MaxPoolSize = 1,
            MinPoolSize = 0,
            NoResetOnClose = true,
            MaxAutoPrepare = 20,
            AutoPrepareMinUsages = 1,
            ApplicationName = _schema
        };
        _source = NpgsqlDataSource.Create(settings.ConnectionString);
        _provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await _source.OpenConnectionAsync(call.Arg<CancellationToken>()));
        await using var setup = await _source.OpenConnectionAsync();
        _backendPid = setup.ProcessID;
        await using var command = new NpgsqlCommand(
            "SET jit = on; SET max_parallel_workers_per_gather = 2; SET plan_cache_mode = force_generic_plan", setup);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
        await fixture.DropSchemaAsync(_schema);
    }

    [IntegrationTheory]
    [InlineData(false, "on")]
    [InlineData(true, "off")]
    public async Task CountAsync_SpatialCount_UsesOptInSettingAndRestoresPooledSession(bool enabled, string expected)
    {
        var reader = CreateReader(enabled);
        (await reader.CountAsync(1, BboxQuery())).Should().Be(3, "bbox edges are included; null and empty points are excluded");
        await AssertObservedAsync(expected);
        await AssertSessionRestoredAsync();

        var filtered = BboxQuery() with { EnforcedSqlFilter = new SqlFragment("\"id\" >= @p0", [2L]) };
        (await reader.CountAsync(1, filtered)).Should().Be(2, "security parameters must survive batching");
        var empty = BboxQuery() with
        {
            SpatialFilter = BboxQuery().SpatialFilter!.Value with { EnvelopeMinX = 20, EnvelopeMaxX = 21 }
        };
        (await reader.CountAsync(1, empty)).Should().Be(0);
        await AssertSessionRestoredAsync();
    }

    [IntegrationTest]
    public async Task CountAsync_PreparedPlans_KeepOrdinaryAndScopedQueriesSeparate()
    {
        await using (var setupConnection = await _source.OpenConnectionAsync())
        {
            await using var setupCommand = new NpgsqlCommand("SET jit_above_cost = 0", setupConnection);
            await setupCommand.ExecuteNonQueryAsync();
        }
        foreach (var enabled in new[] { false, true, false, true })
        {
            await fixture.ExecuteAsync($"TRUNCATE {_schema}.observations");
            var reader = CreateReader(enabled);
            for (var repetition = 0; repetition < 4; repetition++)
            {
                (await reader.CountAsync(1, BboxQuery())).Should().Be(3);
            }
            await AssertObservedAsync(enabled ? "off" : "on");
            await AssertSessionRestoredAsync();
        }

        await using var connection = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT statement FROM pg_prepared_statements WHERE generic_plans > 0", connection);
        await using var results = await command.ExecuteReaderAsync();
        var statements = new List<string>();
        while (await results.ReadAsync())
        {
            statements.Add(results.GetString(0));
        }
        statements.Should().Contain(sql => sql.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        statements.Should().Contain(sql => sql.StartsWith("SELECT ALL COUNT(*)", StringComparison.Ordinal));
    }

    [IntegrationTheory]
    [InlineData("managed")]
    [InlineData("unknown-geometry")]
    [InlineData("explicit-polygon")]
    [InlineData("nonspatial")]
    public async Task CountAsync_OutsideScopedProfile_KeepsOrdinaryPlanning(string shape)
    {
        var query = shape switch
        {
            "explicit-polygon" => BboxQuery() with { SpatialFilter = BboxQuery().SpatialFilter!.Value with { IsSimpleEnvelope = false } },
            "nonspatial" => BboxQuery() with { SpatialFilter = null },
            _ => BboxQuery()
        };
        var baseline = await CreateReader(false).CountAsync(1, query);
        var reader = CreateReader(true, sourceBacked: shape != "managed", knownGeometry: shape != "unknown-geometry");
        (await reader.CountAsync(1, query)).Should().Be(baseline);
        await AssertObservedAsync("on");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTest]
    public async Task CountAsync_BorrowedTransaction_DoesNotAlterCallerSettingsOrLifetime()
    {
        await PostgresMutationTransaction.ExecuteAsync(_provider, async () =>
        {
            await using var lease = await _provider.OpenNpgsqlConnectionAsync();
            lease.Transaction.Should().NotBeNull();
            (await CreateReader(true).CountAsync(1, BboxQuery())).Should().Be(3);
            await using var command = new NpgsqlCommand("SELECT current_setting('jit')", lease);
            (await command.ExecuteScalarAsync()).Should().Be("on");
            return true;
        }, _ => true, CancellationToken.None);
        await AssertObservedAsync("on");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTest]
    public async Task CountAsync_AmbientTransaction_KeepsOrdinaryPlanning()
    {
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (await CreateReader(true).CountAsync(1, BboxQuery())).Should().Be(3);
            scope.Complete();
        }
        await AssertObservedAsync("on");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTest]
    public async Task QueryPageAsync_EnabledCountProfile_DoesNotChangeFeatureReadPlanning()
    {
        var page = await CreateReader(true).QueryPageAsync(1, BboxQuery());
        page.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L);
        await AssertObservedAsync("on");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTest]
    public async Task CountAsync_SqlError_RollsBackLocalSettingOnSamePooledConnection()
    {
        await ReplaceProbeAsync("IF current_setting('jit') = 'off' THEN RAISE EXCEPTION 'count probe failure'; END IF;");
        var call = () => CreateReader(true).CountAsync(1, BboxQuery());
        (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        await AssertSessionRestoredAsync();
        (await CreateReader(false).CountAsync(1, BboxQuery())).Should().Be(3);
    }

    [IntegrationTest]
    public async Task CountAsync_CancelledQuery_RestoresSessionAndReleasesLease()
    {
        await ReplaceProbeAsync("IF current_setting('jit') = 'off' THEN PERFORM pg_sleep(30); END IF;");
        using var cancellation = new CancellationTokenSource();
        var pending = CreateReader(true).CountAsync(1, BboxQuery(), cancellation.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var sleeping = false;
            while (!sleeping && !pending.IsCompleted)
            {
                await using var monitor = await fixture.DataSource.OpenConnectionAsync(deadline.Token);
                await using var command = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = $1 AND wait_event = 'PgSleep')", monitor);
                command.Parameters.AddWithValue(_schema);
                sleeping = (bool)(await command.ExecuteScalarAsync(deadline.Token))!;
                if (!sleeping)
                {
                    await Task.Delay(25, deadline.Token);
                }
            }
            sleeping.Should().BeTrue("the count must reach the probe with JIT disabled before cancellation");
            await cancellation.CancelAsync();
            await FluentActions.Awaiting(async () =>
            {
                await pending;
            }).Should().ThrowAsync<OperationCanceledException>();
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
            }
        }
        await AssertSessionRestoredAsync();
        (await CreateReader(false).CountAsync(1, BboxQuery())).Should().Be(3);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountedRead_CountUsesScopedSettingAndFeaturesUseOrdinarySetting(bool unlimitedPage)
    {
        var reader = CreateReader(true);
        if (unlimitedPage)
        {
            var result = await reader.QueryPageAsync(1, BboxQuery() with { Limit = null });
            result.TotalCount.Should().Be(3);
            result.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L);
        }
        else
        {
            // A full first page still needs a count; a short page can now prove
            // its total directly and intentionally never enters the count batch.
            var result = await reader.QueryAsync(1, BboxQuery() with { Limit = 3 });
            result.TotalCount.Should().Be(3);
            result.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L);
        }
        await AssertSessionRestoredAsync();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT array_agg(DISTINCT jit ORDER BY jit) FROM {_schema}.observations", connection);
        ((string[])(await command.ExecuteScalarAsync())!).Should().Equal("off", "on");
    }

    [IntegrationTest]
    public async Task ShortPage_ReusesExactTotalWithoutApplyingCountPlannerSetting()
    {
        var result = await CreateReader(true).QueryAsync(1, BboxQuery());

        result.TotalCount.Should().Be(3);
        result.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L);
        result.HasMoreResults.Should().BeFalse();
        await AssertObservedAsync("on");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTheory]
    [InlineData(null, "on")]
    [InlineData("true", "off")]
    public async Task RegisteredFeatureStore_ConfigurationReachesBoundReader(string? setting, string expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString
        };
        if (setting is not null)
        {
            values["Database:DisableJitForSourceSpatialCounts"] = setting;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        Honua.Db.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(
            services, configuration, TestCoreSchemaMigrations.Manifest);
        services.AddSingleton(Substitute.For<IMetadataV2GraphProvider>());
        services.AddSingleton(Substitute.For<IConnectionEncryptionService>());
        services.AddSingleton(Substitute.For<ISecureConnectionResolver>());
        services.AddSingleton(Substitute.For<IConnectionSecretResolver>());
        services.AddSingleton(_provider);
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = container.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFeatureDataProvider>();
        var resource = CreateResource(knownGeometry: true);
        var binding = new FeatureProviderBinding(new MetadataV2Service(), resource, new MetadataV2Publication(),
            new MetadataV2StorageBinding { ResourceId = resource.Metadata.Id }, CreateMapping(sourceBacked: true), 1, store, Connection: null);
        var reader = ((IBindableFeatureDataProvider)store).CreateReaderForBinding(binding);
        (await reader.CountAsync(1, BboxQuery())).Should().Be(3);
        await AssertObservedAsync(expected);
        await AssertSessionRestoredAsync();
    }

    [IntegrationTest]
    public async Task CountAsync_SessionAlreadyJitDisabled_PreservesOriginalSetting()
    {
        await using (var connection = await _source.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("SET jit = off", connection);
            await command.ExecuteNonQueryAsync();
        }
        (await CreateReader(true).CountAsync(1, BboxQuery())).Should().Be(3);
        await AssertObservedAsync("off");
        await AssertSessionRestoredAsync("off");
    }

    private Task ReplaceProbeAsync(string statement) => fixture.ExecuteAsync($$"""
        CREATE OR REPLACE FUNCTION {{_schema}}.observe_geom(value geometry) RETURNS geometry
        LANGUAGE plpgsql VOLATILE AS $body$ BEGIN {{statement}} RETURN value; END $body$;
        """);

    private async Task AssertObservedAsync(string expected)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT DISTINCT jit, workers FROM {_schema}.observations", connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the source geometry must be evaluated");
        reader.GetString(0).Should().Be(expected);
        reader.GetString(1).Should().Be("2", "the JIT profile must not force serial planning");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    private async Task AssertSessionRestoredAsync(string expectedJit = "on")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await _source.OpenConnectionAsync(deadline.Token);
        connection.ProcessID.Should().Be(_backendPid, "the original physical connection should remain reusable");
        await using var command = new NpgsqlCommand("SELECT current_setting('jit'), current_setting('max_parallel_workers_per_gather')", connection);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        (await reader.ReadAsync(deadline.Token)).Should().BeTrue();
        reader.GetString(0).Should().Be(expectedJit);
        reader.GetString(1).Should().Be("2");
    }

    private static FeatureQuery BboxQuery() => new()
    {
        Limit = 100,
        SpatialFilter = new SpatialFilter
        {
            Geometry = new WKTReader().Read("POLYGON((-1 -1,-1 1,1 1,1 -1,-1 -1))").AsBinary(),
            Srid = 4326,
            IsSimpleEnvelope = true,
            EnvelopeMinX = -1,
            EnvelopeMinY = -1,
            EnvelopeMaxX = 1,
            EnvelopeMaxY = 1,
            SpatialRelationship = SpatialRelationship.Intersects
        }
    };

    private PostgresStorageMappedFeatureReader CreateReader(bool enabled, bool sourceBacked = true, bool knownGeometry = true) => new(
        _provider,
        new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
        CreateResource(knownGeometry), CreateMapping(sourceBacked),
        connection: null, connectionEncryptionService: null, disableJitForSourceSpatialCounts: enabled);

    private static MetadataV2Resource CreateResource(bool knownGeometry) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "jit-count", Name = "jit-count" },
        Spatial = knownGeometry ? new MetadataV2ResourceSpatial { GeometryType = MetadataV2GeometryType.Point } : null,
        SchemaFields = [new() { Name = "id", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] }]
    };

    private FeatureStorageMapping CreateMapping(bool sourceBacked) => new(
        "read_points", SchemaName: _schema, PrimaryKeyColumn: "id", GeometryColumn: "geom", StorageSrid: 4326,
        ProviderOptions: new Dictionary<string, string> { [FeatureStorageMapping.SourceBackedOption] = sourceBacked.ToString() });
}
