// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Transactions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed partial class PostgresStorageMappedSpatialCountSerialIntegrationTests
{
    [IntegrationTest]
    public async Task AutomaticBoundedRead_DefaultPoliciesScopePageAndCountOnly()
    {
        await using var services = CreatePlanningServices(null, null);
        await using var scope = services.CreateAsyncScope();
        var reader = CreateRegisteredPlanningReader(scope.ServiceProvider);

        await AssertCountedPageAsync(reader);
        await AssertObservedAsync("0");
        await AssertSessionRestoredAsync();

        // An identical standalone count must retain database defaults, even
        // after this backend has prepared an automatically scoped page/count.
        await ClearPlanningObservationsAsync();
        (await reader.CountAsync(1, BboxQuery() with { Limit = 3 })).Should().Be(3);
        await AssertObservedAsync("2");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTheory]
    [InlineData(null, "false", "0,2", "2")]
    [InlineData(null, "true", "0", "0")]
    [InlineData("false", null, "0,2", "2")]
    [InlineData("false", "false", "2", "2")]
    [InlineData("false", "true", "0,2", "0")]
    [InlineData("true", null, "0", "2")]
    [InlineData("true", "false", "0,2", "2")]
    [InlineData("true", "true", "0", "0")]
    public async Task AutomaticBoundedRead_ExplicitOverridesRemainIndependent(
        string? reads, string? counts, string expectedPageWorkers, string expectedStandaloneWorkers)
    {
        await using var services = CreatePlanningServices(reads, counts);
        await using var scope = services.CreateAsyncScope();
        var reader = CreateRegisteredPlanningReader(scope.ServiceProvider);

        await AssertCountedPageAsync(reader);
        await using (var connection = await _source.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                $"SELECT array_agg(DISTINCT workers ORDER BY workers), bool_and(jit = 'on') FROM {_schema}.observations", connection);
            await using var observed = await command.ExecuteReaderAsync();
            (await observed.ReadAsync()).Should().BeTrue();
            observed.GetFieldValue<string[]>(0).Should().Equal(expectedPageWorkers.Split(','));
            observed.GetBoolean(1).Should().BeTrue("automatic serial planning must not change PostgreSQL JIT");
        }
        await AssertSessionRestoredAsync();

        await ClearPlanningObservationsAsync();
        (await reader.CountAsync(1, BboxQuery())).Should().Be(3);
        await AssertObservedAsync(expectedStandaloneWorkers);
        await AssertSessionRestoredAsync();
    }

    [IntegrationTheory]
    [InlineData("unbounded")]
    [InlineData("large-page")]
    [InlineData("later-page")]
    [InlineData("descending")]
    [InlineData("include-null")]
    [InlineData("explicit-polygon")]
    [InlineData("nonspatial")]
    [InlineData("distinct")]
    [InlineData("contains")]
    [InlineData("managed")]
    [InlineData("unknown-geometry")]
    [InlineData("line-metadata")]
    public async Task AutomaticBoundedRead_IneligibleShapesKeepDatabasePlanning(string shape)
    {
        await using var services = CreatePlanningServices(null, null);
        await using var scope = services.CreateAsyncScope();
        var reader = CreateRegisteredPlanningReader(scope.ServiceProvider, shape);
        var query = BboxQuery() with { Limit = 3 };
        query = shape switch
        {
            "unbounded" => query with { Limit = null },
            "large-page" => query with { Limit = 101 },
            "later-page" => query with { Offset = 1 },
            "descending" => query with { OrderBy = ImmutableArray.Create(new OrderByClause("id", ascending: false)) },
            "include-null" => query with { IncludeNullGeometry = true },
            "explicit-polygon" => query with { SpatialFilter = query.SpatialFilter!.Value with { IsSimpleEnvelope = false } },
            "nonspatial" => query with { SpatialFilter = null },
            "distinct" => query with { Distinct = true },
            "contains" => query with { SpatialFilter = query.SpatialFilter!.Value with { SpatialRelationship = SpatialRelationship.Contains } },
            _ => query
        };

        await using var controls = CreatePlanningServices("false", "false");
        await using var controlScope = controls.CreateAsyncScope();
        var control = CreateRegisteredPlanningReader(controlScope.ServiceProvider, shape);
        var expected = await control.QueryAsync(1, query);
        await ClearPlanningObservationsAsync();

        var result = await reader.QueryAsync(1, query);
        result.TotalCount.Should().Be(expected.TotalCount);
        result.Items.Select(feature => feature.Id).Should().Equal(expected.Items.Select(feature => feature.Id));
        result.HasMoreResults.Should().Be(expected.HasMoreResults);
        await AssertObservedAsync("2");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBoundedRead_CallerTransactionsKeepDatabasePlanning(bool ambient)
    {
        await using var services = CreatePlanningServices(null, null);
        await using var serviceScope = services.CreateAsyncScope();
        var reader = CreateRegisteredPlanningReader(serviceScope.ServiceProvider);
        if (ambient)
        {
            using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await AssertCountedPageAsync(reader);
            transaction.Complete();
        }
        else
        {
            await PostgresMutationTransaction.ExecuteAsync(_provider, async () =>
            {
                await AssertCountedPageAsync(reader);
                return true;
            }, _ => true, CancellationToken.None);
        }
        await AssertObservedAsync("2");
        await AssertSessionRestoredAsync();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBoundedRead_SqlErrorRestoresPooledSession(bool countOnly)
    {
        await using var services = CreatePlanningServices(countOnly ? "false" : null, null);
        await using var scope = services.CreateAsyncScope();
        var reader = CreateRegisteredPlanningReader(scope.ServiceProvider);
        await ReplaceProbeAsync("IF current_setting('max_parallel_workers_per_gather') = '0' THEN RAISE EXCEPTION 'automatic planning probe failure'; END IF;");

        var call = () => reader.QueryAsync(1, BboxQuery() with { Limit = 3 });
        (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        await AssertSessionRestoredAsync();
        // Standalone counts must remain ordinary on the same pooled backend.
        (await reader.CountAsync(1, BboxQuery())).Should().Be(3);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticBoundedRead_CancellationRestoresPooledSession(bool countOnly)
    {
        await using var services = CreatePlanningServices(countOnly ? "false" : null, null);
        await using var scope = services.CreateAsyncScope();
        var reader = CreateRegisteredPlanningReader(scope.ServiceProvider);
        await ReplaceProbeAsync("IF current_setting('max_parallel_workers_per_gather') = '0' THEN PERFORM pg_sleep(30); END IF;");
        using var cancellation = new CancellationTokenSource();
        var pending = reader.QueryAsync(1, BboxQuery() with { Limit = 3 }, cancellation.Token);
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
            sleeping.Should().BeTrue("the automatic read or associated count must reach its scoped query");
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
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                pending.IsCanceled.Should().BeTrue();
            }
        }
        await AssertSessionRestoredAsync();
        (await reader.CountAsync(1, BboxQuery())).Should().Be(3);
    }

    private ServiceProvider CreatePlanningServices(string? reads, string? counts)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString
        };
        if (reads is not null)
        {
            values["Database:PreferSerialBoundedSpatialReads"] = reads;
        }
        if (counts is not null)
        {
            values["Database:PreferSerialSourceSpatialCounts"] = counts;
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
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private IFeatureReader CreateRegisteredPlanningReader(IServiceProvider services, string? shape = null)
    {
        var store = services.GetRequiredService<IFeatureDataProvider>();
        var resource = CreateResource(shape != "unknown-geometry",
            shape == "line-metadata" ? MetadataV2GeometryType.LineString : MetadataV2GeometryType.Point);
        var binding = new FeatureProviderBinding(new MetadataV2Service(), resource, new MetadataV2Publication(),
            new MetadataV2StorageBinding { ResourceId = resource.Metadata.Id }, CreateMapping(shape != "managed"),
            1, store, Connection: null);
        return ((IBindableFeatureDataProvider)store).CreateReaderForBinding(binding);
    }

    private static async Task AssertCountedPageAsync(IFeatureReader reader)
    {
        // A full page forces the associated exact-count path to execute.
        var result = await reader.QueryAsync(1, BboxQuery() with { Limit = 3 });
        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L, 3L);
        result.TotalCount.Should().Be(3);
        result.HasMoreResults.Should().BeFalse();
    }

    private async Task ClearPlanningObservationsAsync()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"TRUNCATE {_schema}.observations", connection);
        await command.ExecuteNonQueryAsync();
    }
}
