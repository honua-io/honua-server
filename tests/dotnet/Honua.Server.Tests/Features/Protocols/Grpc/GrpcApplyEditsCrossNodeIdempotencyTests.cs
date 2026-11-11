// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Two server nodes with no shared memory over one PostgreSQL database: the receipt row
/// written in the edit transaction decides which keyed applyEdits commits, and the other
/// node replays that receipt.
/// </summary>
[Collection("Database.CoreFeatureStore")]
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcApplyEditsCrossNodeIdempotencyTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await database.CreateIsolatedSchemaAsync(nameof(GrpcApplyEditsCrossNodeIdempotencyTests));
        await database.ExecuteAsync(
            """
            CREATE TABLE features (
                objectid bigserial PRIMARY KEY,
                layer_id integer NOT NULL,
                geometry geometry,
                attributes jsonb NOT NULL DEFAULT '{}'::jsonb
            );
            """,
            _schema);
        await database.ExecuteAsync(await ReceiptMigrationAsync(), _schema);
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameKeyOnTwoNodes_CommitsOnce_AndTheOtherNodeReplaysTheFirstReceipt()
    {
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");
        var nodeA = Node();
        var nodeB = Node();

        var original = await nodeA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), nodeA.Context(user, tenantId: "tenant-a"));
        var retry = await nodeB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), nodeB.Context(user, tenantId: "tenant-a"));
        var otherTenant = await nodeB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), nodeB.Context(user, tenantId: "tenant-b"));

        original.AddResults.Should().ContainSingle().Which.Success.Should().BeTrue();
        retry.AddResults.Should().ContainSingle().Which.ObjectId.Should().Be(original.AddResults[0].ObjectId);
        otherTenant.AddResults.Should().ContainSingle().Which.ObjectId.Should().NotBe(original.AddResults[0].ObjectId);
        (await FeatureCountAsync()).Should().Be(2, "only the other tenant's keyed edit executes again");
    }

    [IntegrationTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameKeyRacingOnTwoNodes_TheLaterCommitIsRolledBack_AndReplaysTheFirstReceipt()
    {
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");
        var secondSnapshotTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodeA = Node();
        var nodeB = Node(new PausingConnectionProvider(Connections(), async () =>
        {
            secondSnapshotTaken.TrySetResult();
            await firstCommitted.Task.ConfigureAwait(false);
        }));

        // Node B finds no receipt and opens its edit transaction; node A then commits the
        // same key before B reaches its own receipt insert.
        var racing = nodeB.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), nodeB.Context(user, tenantId: null));
        await secondSnapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var original = await nodeA.Service.ApplyEdits(
            GrpcApplyEditsIdempotencyTests.AddRequest(), nodeA.Context(user, tenantId: null));
        firstCommitted.SetResult();
        var replayed = await racing.WaitAsync(TimeSpan.FromSeconds(30));

        original.AddResults.Should().ContainSingle().Which.Success.Should().BeTrue();
        replayed.AddResults.Should().ContainSingle().Which.Success.Should().BeTrue();
        replayed.AddResults[0].ObjectId.Should().Be(original.AddResults[0].ObjectId);
        (await FeatureCountAsync()).Should().Be(1, "the keyed edit must commit exactly once across nodes");
    }

    private GrpcApplyEditsIdempotencyTests.ServiceHarness Node(IAdoNetDatabaseConnectionProvider? connections = null)
    {
        var featureStore = FeatureStore(connections ?? Connections());
        return new(
            new GrpcApplyEditsReceipts(new PostgresFeatureEditIdempotencyReceipts(database.DataSource, _schema)),
            featureStore,
            featureStore);
    }

    private TestDatabaseConnectionProvider Connections() => new(database.DataSource, () => _schema);

    private PostgresFeatureStoreRefactored FeatureStore(IAdoNetDatabaseConnectionProvider connections)
    {
        var pools = new DefaultObjectPoolProvider();
        var geometryProcessor = new GeometryProcessor();
        var cacheManager = new FeatureCacheManager(connections, NullLogger<FeatureCacheManager>.Instance, _schema);
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            connections,
            geometryProcessor,
            cacheManager,
            pools.Create(new DictionaryPooledObjectPolicy()),
            StatementCache: null,
            Logger: NullLogger<FeatureDataAccess>.Instance,
            PerformanceOptions: null,
            LimitsOptions: null,
            PerformanceMonitor: null,
            SchemaName: _schema));
        var queryBuilder = new FeatureQueryBuilder(
            pools.Create(new Microsoft.Extensions.ObjectPool.StringBuilderPooledObjectPolicy()), geometryProcessor, _schema);
        return new PostgresFeatureStoreRefactored(queryBuilder, dataAccess, cacheManager);
    }

    [IntegrationTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/ApplyEdits")]
    public async Task ApplyEdits_SameKeyUpdatingTheSameRowsOnTwoNodes_TheConflictingNodeReplaysTheFirstReceipt()
    {
        var targets = new[] { await SeedFeatureAsync(), await SeedFeatureAsync() };
        var user = GrpcApplyEditsIdempotencyTests.Subject("editor", issuer: "https://issuer-a.example");
        var secondSnapshotTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodeA = Node();
        var nodeB = Node(new PausingConnectionProvider(Connections(), async () =>
        {
            secondSnapshotTaken.TrySetResult();
            await firstCommitted.Task.ConfigureAwait(false);
        }));

        // Node B's repeatable-read snapshot predates node A's commit, so B's own update of
        // the same rows fails with a serialization failure before it reaches its receipt.
        var racing = nodeB.Service.ApplyEdits(UpdateRequest(targets), nodeB.Context(user, tenantId: null));
        await secondSnapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var original = await nodeA.Service.ApplyEdits(UpdateRequest(targets), nodeA.Context(user, tenantId: null));
        firstCommitted.SetResult();
        var replayed = await racing.WaitAsync(TimeSpan.FromSeconds(30));

        original.UpdateResults.Should().HaveCount(2).And.OnlyContain(result => result.Success);
        replayed.Should().Be(original, "the node that lost the race replays the committed receipt");
    }

    private static Proto.ApplyEditsRequest UpdateRequest(IEnumerable<long> objectIds)
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0,
            RollbackOnFailure = true,
            IdempotencyKey = "update-retry-key",
        };
        foreach (var objectId in objectIds)
        {
            request.Updates.Add(new Proto.Feature
            {
                Id = objectId,
                Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "renamed" } },
            });
        }

        return request;
    }

    private async Task<long> SeedFeatureAsync()
    {
        await using var command = database.DataSource.CreateCommand(
            $$"""INSERT INTO {{_schema}}.features (layer_id, attributes) VALUES (0, '{"name": "original"}') RETURNING objectid""");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> FeatureCountAsync()
    {
        await using var command = database.DataSource.CreateCommand($"SELECT count(*) FROM {_schema}.features");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReceiptMigrationAsync()
    {
        var assembly = typeof(HonuaFeatureService).Assembly;
        var name = assembly.GetManifestResourceNames().Single(
            resource => resource.EndsWith("130_CreateGrpcApplyEditsReceipt.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return (await reader.ReadToEndAsync()).Replace("$HonuaSchema$.", string.Empty, StringComparison.Ordinal);
    }

    // Takes the edit transaction's snapshot, then holds it open until the caller lets it continue.
    private sealed class PausingConnectionProvider(
        IAdoNetDatabaseConnectionProvider inner,
        Func<Task> afterSnapshot) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => inner.GetConnectionString();

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => inner.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var opened = await inner.OpenTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            await using (var command = opened.Connection.CreateCommand())
            {
                command.Transaction = opened.Transaction;
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            await afterSnapshot().ConfigureAwait(false);
            return opened;
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => inner.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => inner.ExecuteWithDeadlockRetryAsync(operation, cancellationToken);
    }
}
