// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Cache)]
public sealed class ExternalLayerChangeWorkerTests(DatabaseFixtureAdapter fixture)
{
    [IntegrationTest]
    public Task PurgeFails_ChangeRemainsQueuedUntilAnotherHostSuccessfullyRetries()
        => InSchemaAsync(async database =>
        {
            var cdn = Cdn(CdnInvalidationOutcome.Failed, CdnInvalidationOutcome.Submitted);
            using var first = Worker(database, cdn);
            (await first.ProcessBatchAsync(default)).Should().Be(0);
            (await CountAsync(database)).Should().Be(1L);
            using var second = Worker(database, cdn);
            (await second.ProcessBatchAsync(default)).Should().Be(1);
            (await CountAsync(database)).Should().Be(0L);
            (await second.ProcessBatchAsync(default)).Should().Be(0);
            await cdn.Received(2).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task FailedPurge_BacksOffOnThatTaskInsteadOfRetryingEveryLoop()
        => InSchemaAsync(async database =>
        {
            var cdn = Cdn(CdnInvalidationOutcome.Failed);
            using var worker = Worker(database, cdn);
            await worker.ProcessBatchAsync(default);
            await worker.ProcessBatchAsync(default);
            await worker.ProcessBatchAsync(default);
            await cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
            (await CountAsync(database)).Should().Be(1L);
        });

    [IntegrationTest]
    public Task ManyChangesToOneLayer_AreCoalescedIntoOnePurge()
        => InSchemaAsync(async database =>
        {
            for (var change = 0; change < 4; change++)
            {
                await RecordAsync(database);
            }

            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            using var worker = Worker(database, cdn);
            (await worker.ProcessBatchAsync(default)).Should().Be(5);
            (await CountAsync(database)).Should().Be(0L);
            await cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task FailingLayer_DoesNotHoldUpAnotherLayer()
        => InSchemaAsync(async database =>
        {
            await RegisterAsync(database, 8);
            await RecordAsync(database, 8);
            var cdn = Cdn(CdnInvalidationOutcome.Failed, CdnInvalidationOutcome.Submitted);
            using var worker = Worker(database, cdn);
            (await worker.ProcessBatchAsync(default)).Should().Be(1);
            (await CountAsync(database)).Should().Be(1L, "only the failing layer's change stays queued");
        });

    [IntegrationTest]
    public Task LayerClaimedByAnotherTask_IsLeftForThatTask()
        => InSchemaAsync(async database =>
        {
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            var leases = Substitute.For<ILeaseStore>();
            leases.TryAcquireAsync(default, default, default).ReturnsForAnyArgs(false);
            using var worker = Worker(database, cdn, leases);
            (await worker.ProcessBatchAsync(default)).Should().Be(0);
            await leases.Received(1).TryAcquireAsync(Arg.Is<LeaseClaim>(claim => claim.LeaseKey == "external-layer-purge:7"),
                ExternalLayerChangeWorker.PurgeWindow, Arg.Any<CancellationToken>());
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
            (await CountAsync(database)).Should().Be(1L);
        });

    [UnitTheory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(4, 480)]
    [InlineData(5, 900)]
    [InlineData(40, 900)]
    public void Backoff_DoublesFromTheWindowUpToFifteenMinutes(int failures, int seconds)
        => ExternalLayerChangeWorker.Backoff(failures).Should().Be(TimeSpan.FromSeconds(seconds));

    [IntegrationTest]
    public Task PendingChanges_AreReadAsOneRowPerLayerCoveringEveryExtent()
        => InSchemaAsync(async database =>
        {
            await RegisterAsync(database, 8);
            await RecordAsync(database, 8, [1d, 2d, 3d, 4d]);
            await RecordAsync(database, 8, [-1d, 3d, 2d, 6d]);
            using var worker = Worker(database, Cdn(CdnInvalidationOutcome.Submitted));

            var pending = await worker.ReadBatchAsync(default);

            pending.Select(change => change.Layer).Should().Equal(7, 8);
            pending[0].Extent.Should().BeNull("layer 7's change has no extent");
            pending[1].Revision.Should().Be(2);
            pending[1].Extent!.Value.ToArray().Should().Equal(-1d, 2d, 3d, 6d);
        });

    [IntegrationTest]
    public Task RevisionRead_IsPinnedWithinRequestAndRefreshesOnTheNextRequest()
        => InSchemaAsync(async database =>
        {
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            var source = new PostgresExternalLayerRevisions(() => database, accessor);
            var before = await source.GetRevisionAsync();
            await RecordAsync(database);
            (await source.GetRevisionAsync()).Should().Be(before);
            accessor.HttpContext = new DefaultHttpContext();
            (await source.GetRevisionAsync()).Should().NotBe(before);
            accessor.HttpContext = null;
        });

    [IntegrationTest]
    public Task RevisionRead_TooManyTrackedLayers_RefusesPartialGeneration()
        => InSchemaAsync(async database =>
        {
            await database.ExecuteAsync(async (connection, token) =>
            {
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO {database.Table("external_layer_revisions")} (layer_id)
                    SELECT generate_series(8, 1031)
                    """, connection);
                await command.ExecuteNonQueryAsync(token);
            }, StatementRepeat.SafeToRepeat, default);
            var source = new PostgresExternalLayerRevisions(() => database, new HttpContextAccessor());
            var read = () => source.GetRevisionAsync();
            await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1024*");
        });

    private async Task InSchemaAsync(Func<JobStoreDatabase, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ExternalLayerChangeWorkerTests));
        try
        {
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "165_CreateExternalLayerChanges.sql"));
            await using var command = fixture.DataSource.CreateCommand(
                sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal)
                + $"INSERT INTO \"{schema}\".external_layer_revisions (layer_id, enabled) VALUES (7, true);");
            await command.ExecuteNonQueryAsync();
            var database = new JobStoreDatabase(fixture.DataSource, schema);
            await RecordAsync(database);
            await test(database);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static Task RecordAsync(JobStoreDatabase database, int layerId = 7, double[]? extent = null)
        => database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT {database.Table("record_external_layer_change")}(@layer, @extent)", connection);
            command.Parameters.AddWithValue("layer", layerId);
            command.Parameters.Add(new NpgsqlParameter("extent", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double)
            {
                Value = (object?)extent ?? DBNull.Value,
            });
            await command.ExecuteNonQueryAsync(token);
        }, StatementRepeat.NotAfterSend, default);

    private static Task RegisterAsync(JobStoreDatabase database, int layerId)
        => database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {database.Table("external_layer_revisions")} (layer_id, enabled) VALUES (@layer, true)", connection);
            command.Parameters.AddWithValue("layer", layerId);
            await command.ExecuteNonQueryAsync(token);
        }, StatementRepeat.NotAfterSend, default);

    private static ICdnInvalidator Cdn(params CdnInvalidationOutcome[] outcomes)
    {
        var cdn = Substitute.For<ICdnInvalidator>();
        var results = outcomes.Select(outcome => new CdnInvalidationResult(outcome, outcome.ToString())).ToArray();
        cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(results[0], results.Skip(1).ToArray());
        return cdn;
    }

    private static ExternalLayerChangeWorker Worker(JobStoreDatabase database, ICdnInvalidator cdn, ILeaseStore? leases = null)
    {
        var collection = new ServiceCollection();
        if (leases is not null)
        {
            collection.AddSingleton(leases);
        }

        var services = collection.BuildServiceProvider();
        var invalidation = new OutputCacheInvalidationService(null, null, null,
            services.GetRequiredService<IServiceScopeFactory>(), null,
            NullLogger<OutputCacheInvalidationService>.Instance,
            new CdnInvalidationService(cdn, NullLogger<CdnInvalidationService>.Instance));
        return new(database, invalidation, services);
    }

    private static Task<long> CountAsync(JobStoreDatabase database)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {database.Table("external_layer_changes")}", connection);
            return (long)(await command.ExecuteScalarAsync(token))!;
        }, StatementRepeat.SafeToRepeat, default);
}
