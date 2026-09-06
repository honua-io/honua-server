// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database")]
public sealed class PostgresChangeTrackerTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public PostgresChangeTrackerTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task ReplicaUpload_BatchWithoutOutbox_StampsEveryRowAndRestoresOrigin()
    {
        var schemaContext = (Honua.Infrastructure.Middleware.SchemaContext)_fixture
            .GetService<Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext>();
        var previousSchema = schemaContext.CurrentSchema;
        schemaContext.CurrentSchema = _fixture.CurrentSchema;
        try
        {
            var writer = _fixture.GetService<IFeatureWriter>();
            var reader = _fixture.GetService<IFeatureReader>();
            var tracker = _fixture.GetService<IChangeTracker>();
            var baseline = await tracker.GetCurrentGenerationAsync();
            Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.Current.Should().BeNull();
            Feature MakeFeature(string name, double x, double y) => Feature.Create(0,
                new NetTopologySuite.IO.WKBWriter().Write(new NetTopologySuite.Geometries.Point(x, y) { SRID = 4326 }),
                System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty.Add("name", name));

            FeatureEditResult uploaded;
            using (ReplicaUploadOriginScope.Begin("bulk-replica"))
            {
                uploaded = await writer.ApplyEditsAsync(0, new FeatureEditBatch
                {
                    Creates = [MakeFeature("bulk-one", 10, 20), MakeFeature("bulk-two", 30, 40)],
                    RollbackOnFailure = false
                });
            }
            uploaded.CreatedCount.Should().Be(2, string.Join("; ", uploaded.CreateResults.Select(result => result.ErrorMessage)));
            uploaded.CreatedIds.Should().OnlyHaveUniqueItems();
            ReplicaUploadOriginScope.Current.Should().BeNull();
            var foreign = await writer.ApplyEditsAsync(0, new FeatureEditBatch
            {
                Creates = [MakeFeature("foreign-after-bulk", 50, 60)],
                RollbackOnFailure = false
            });
            foreign.CreatedCount.Should().Be(1);
            var ownDelta = await tracker.GetChangesSinceAsync(baseline, [0], null, "bulk-replica");
            ownDelta.Should().ContainSingle().Which.ObjectId.Should().Be(foreign.CreatedIds.Single());
            ownDelta.Single().OriginReplicaId.Should().BeNull();
            var otherDelta = await tracker.GetChangesSinceAsync(baseline, [0], null, "other-replica");
            otherDelta.Where(change => change.OriginReplicaId == "bulk-replica")
                .Select(change => change.ObjectId).Should().BeEquivalentTo(uploaded.CreatedIds);
            for (var index = 0; index < 2; index++)
            {
                var stored = await reader.GetAsync(0, uploaded.CreatedIds[index]);
                stored.Should().NotBeNull();
                stored!.Value.Attributes["name"].Should().Be(index == 0 ? "bulk-one" : "bulk-two");
                var geometry = new NetTopologySuite.IO.WKBReader().Read(stored.Value.Geometry!);
                geometry.Coordinate.X.Should().Be(index == 0 ? 10 : 30);
                geometry.Coordinate.Y.Should().Be(index == 0 ? 20 : 40);
            }
        }
        finally
        {
            schemaContext.CurrentSchema = previousSchema;
        }
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task ReplicaOrigins_CollapseForeignHistory_AndRemainVisibleToOtherReplicas()
    {
        const int layerId = 990112;
        var tracker = _fixture.GetService<IChangeTracker>();
        var baseline = await tracker.GetCurrentGenerationAsync();
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        // These independently specified histories model A's two inserts, a later foreign update,
        // an ordinary foreign insert, and B's insert. Transaction-local origins must not leak.
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT set_config('honua.origin_replica_id', 'replica-A', true);
                INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation)
                VALUES (nextval('honua.sync_generation'), 990112, 8101, 1),
                       (nextval('honua.sync_generation'), 990112, 8102, 1);
                """, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await using (var command = new Npgsql.NpgsqlCommand("""
            INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation)
            VALUES (nextval('honua.sync_generation'), 990112, 8101, 2),
                   (nextval('honua.sync_generation'), 990112, 8103, 1);
            """, connection))
        {
            await command.ExecuteNonQueryAsync();
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT set_config('honua.origin_replica_id', 'replica-B', true);
                INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation)
                VALUES (nextval('honua.sync_generation'), 990112, 8104, 1);
                """, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        // A later partial update by A must not erase the preceding foreign update from its feed.
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT set_config('honua.origin_replica_id', 'replica-A', true);
                INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation)
                VALUES (nextval('honua.sync_generation'), 990112, 8101, 2);
                """, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        var forA = await tracker.GetChangesSinceAsync(baseline, [layerId], null, "replica-A");
        forA.Select(change => (change.ObjectId, change.Operation)).Should().BeEquivalentTo(new[]
        {
            (8101L, FeatureChangeOperation.Update),
            (8103L, FeatureChangeOperation.Insert),
            (8104L, FeatureChangeOperation.Insert)
        });
        forA.Single(change => change.ObjectId == 8101).OriginReplicaId.Should().BeNull();
        forA.Single(change => change.ObjectId == 8103).OriginReplicaId.Should().BeNull();
        forA.Single(change => change.ObjectId == 8104).OriginReplicaId.Should().Be("replica-B");

        var forB = await tracker.GetChangesSinceAsync(baseline, [layerId], null, "replica-B");
        forB.Select(change => (change.ObjectId, change.Operation)).Should().BeEquivalentTo(new[]
        {
            (8101L, FeatureChangeOperation.Insert),
            (8102L, FeatureChangeOperation.Insert),
            (8103L, FeatureChangeOperation.Insert)
        });
        forB.Single(change => change.ObjectId == 8102).OriginReplicaId.Should().Be("replica-A");
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetCurrentGeneration_ReturnsNonNegativeValue()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var gen = await tracker.GetCurrentGenerationAsync();

        gen.Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_EmptyLayerIds_ReturnsEmpty()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var changes = await tracker.GetChangesSinceAsync(0, []);

        changes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_NoChanges_ReturnsEmpty()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var gen = await tracker.GetCurrentGenerationAsync();

        // No changes should exist after the current generation
        var changes = await tracker.GetChangesSinceAsync(gen, [0]);
        changes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_LayerIdFiltering_OnlyReturnsRequestedLayers()
    {
        var tracker = _fixture.GetService<IChangeTracker>();

        // Get changes for a non-existent layer — should be empty
        var changes = await tracker.GetChangesSinceAsync(0, [99999]);
        changes.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_ObjectIdFilter_RestrictsFeedSqlSide()
    {
        var tracker = _fixture.GetService<IChangeTracker>();

        // An id that can never match yields an empty feed (and exercises the SQL-side binding).
        var nonMatching = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long> { long.MaxValue });
        nonMatching.Should().BeEmpty();

        // An empty id set short-circuits to an empty feed.
        var emptyFilter = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long>());
        emptyFilter.Should().BeEmpty();

        // A null filter matches the unfiltered overload; when changes exist, filtering to one of
        // their ids returns exactly that feature's collapsed changes.
        var unfiltered = await tracker.GetChangesSinceAsync(0, [0], objectIds: null);
        var baseline = await tracker.GetChangesSinceAsync(0, [0]);
        unfiltered.Should().BeEquivalentTo(baseline);

        if (unfiltered.Count > 0)
        {
            var targetId = unfiltered[0].ObjectId;
            var filtered = await tracker.GetChangesSinceAsync(0, [0], new HashSet<long> { targetId });
            filtered.Should().NotBeEmpty();
            filtered.Should().OnlyContain(change => change.ObjectId == targetId);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task GetChangesSince_PublicObjectIdFilter_FindsDeletedCustomIdChange()
    {
        const int layerId = 990105;
        const long storageObjectId = 190105;
        const long publicObjectId = 700105;
        var tracker = _fixture.GetService<IChangeTracker>();
        var baseGeneration = await tracker.GetCurrentGenerationAsync();

        _fixture.CurrentSchema.Should().NotBeNullOrWhiteSpace();
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO honua.feature_changes
                    (generation, layer_id, objectid, public_objectid, operation)
                VALUES
                    (nextval('honua.sync_generation'), @layerId, @storageObjectId, @publicObjectId, 3);
                """;
            command.Parameters.AddWithValue("layerId", layerId);
            command.Parameters.AddWithValue("storageObjectId", storageObjectId);
            command.Parameters.AddWithValue("publicObjectId", publicObjectId);
            await command.ExecuteNonQueryAsync();
        }

        var changes = await tracker.GetChangesSinceAsync(
            baseGeneration,
            [layerId],
            new HashSet<long> { publicObjectId });

        changes.Should().ContainSingle()
            .Which.Should().Match<FeatureChange>(change =>
                change.ObjectId == storageObjectId
                && change.PublicObjectId == publicObjectId
                && change.Operation == FeatureChangeOperation.Delete);
    }

    [IntegrationTest]
    [Operation(Operations.ExtractChanges)]
    public async Task ChangeCollapsing_OperationValues_AreValidEnumMembers()
    {
        var tracker = _fixture.GetService<IChangeTracker>();
        var changes = await tracker.GetChangesSinceAsync(0, [0]);

        foreach (var change in changes)
        {
            change.Operation.Should().BeOneOf(
                FeatureChangeOperation.Insert,
                FeatureChangeOperation.Update,
                FeatureChangeOperation.Delete);
        }
    }
}
