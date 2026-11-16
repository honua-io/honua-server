// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Infrastructure.Abstractions;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// Change-feed readers take their committed horizon from honua.feature_change_read_horizon(). A
// later release replaces only that function body, so these tests replace it in their own migrated
// database (never the shared one) and check that the running readers follow it.
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class FeatureChangeReadHorizonTests(FeatureChangeDatabaseFixture database)
    : IClassFixture<FeatureChangeDatabaseFixture>
{
    private const string MigratedBody = """
        CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
        RETURNS BIGINT LANGUAGE sql STABLE
        AS $$ SELECT COALESCE(MAX(generation), 0)::BIGINT FROM honua.feature_changes $$;
        """;

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadHorizon_AsMigrated_IsTheCommittedMaximum()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734301);
        await layer.InsertAsync("""{"name":"horizon"}""");
        var head = await layer.HeadAsync();

        (await ReadHorizonAsync()).Should().Be(head);
        (await CreateTracker().GetCurrentGenerationAsync()).Should().Be(head);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadHorizon_ReplacedBody_IsWhatCursorReadersUse()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734302);
        var before = await layer.HeadAsync();
        await layer.InsertAsync("""{"name":"published"}""");
        var published = await layer.HeadAsync();
        await layer.InsertAsync("""{"name":"not yet published"}""");
        (await layer.HeadAsync()).Should().BeGreaterThan(published);

        await PinHorizonAsync(published);
        try
        {
            (await CreateTracker().GetCurrentGenerationAsync()).Should().Be(published);

            var replicas = new PostgresReplicaRepository(Connections());
            var replica = await replicas.RegisterAtCurrentGenerationAsync(new ReplicaRecord
            {
                ReplicaId = Guid.NewGuid().ToString("N"),
                ReplicaName = "horizon",
                ServiceId = "horizon",
                SyncModel = "perReplica",
                LayerIds = [layer.LayerId],
                CreatedAt = DateTimeOffset.UtcNow,
                LastSyncTime = DateTimeOffset.UtcNow,
                LastSyncGeneration = 0
            });
            replica.LastSyncGeneration.Should().Be(published);
            (await replicas.GetAsync(replica.ReplicaId))!.Value.LastSyncGeneration.Should().Be(published);

            var version = await new PostgresVersionManager(Connections()).CreateAsync(
                new CreateVersionRequest($"horizon_{Guid.NewGuid():N}", "sde", VersionAccess.Public));
            version.CommonAncestorGeneration.Should().Be(published);
            version.BranchGeneration.Should().Be(published);

            var log = CreateLog();
            (await log.ReadHeadAsync(CancellationToken.None)).Should().Be(published);
            var changes = await log.ReadAsync(
                new FeatureChangeLogQuery(before, null, null, [layer.LayerId], 10), CancellationToken.None);
            changes.Select(change => change.Cursor).Should().Equal(published);
        }
        finally
        {
            await ExecuteAsync(MigratedBody);
        }
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadWindow_HorizonBelowNewestChanges_CountsOnlyPublishedChanges()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734303);
        await layer.InsertAsync("""{"name":"first"}""");
        var first = await layer.HeadAsync();
        await layer.InsertAsync("""{"name":"second"}""");
        var published = await layer.HeadAsync();
        await InsertUnpublishedAsync(layer, 3);

        await PinHorizonAsync(published);
        try
        {
            var log = CreateLog(new RealtimeOptions { ResumeMaxEvents = 2 }, TimeProvider.System);
            var window = await log.ReadWindowAsync(CancellationToken.None);
            var cached = await log.ReadWindowAsync(CancellationToken.None);

            window.Head.Should().Be(published);
            window.Oldest.Should().BeLessThanOrEqualTo(first, "the two newest published changes stay resumable");
            cached.Should().Be(window);
        }
        finally
        {
            await ExecuteAsync(MigratedBody);
        }
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadWindow_HorizonBelowNewestChanges_AgesOutOnlyPublishedChanges()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734304);
        await layer.InsertAsync("""{"name":"published"}""");
        var published = await layer.HeadAsync();
        await InsertUnpublishedAsync(layer, 2);

        await PinHorizonAsync(published);
        try
        {
            var afterRetention = new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(30));
            var window = await CreateLog(new RealtimeOptions(), afterRetention).ReadWindowAsync(CancellationToken.None);

            window.Head.Should().Be(published);
            window.Oldest.Should().Be(published + 1);
        }
        finally
        {
            await ExecuteAsync(MigratedBody);
        }
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadHorizon_OpenWriter_DoesNotPublishUntilCommit()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734305);
        await layer.InsertAsync("""{"name":"committed"}""");
        var committed = await layer.HeadAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO honua.features (layer_id, attributes)
            VALUES (@layer, '{"name":"in flight"}'::jsonb)
            RETURNING objectid
            """, connection, transaction);
        insert.Parameters.AddWithValue("layer", layer.LayerId);
        var objectId = (long)(await insert.ExecuteScalarAsync())!;

        (await ReadHorizonAsync()).Should().Be(committed);
        (await CreateLog().ReadAsync(
            new FeatureChangeLogQuery(committed, null, null, [layer.LayerId], 10), CancellationToken.None))
            .Should().BeEmpty();

        await transaction.CommitAsync();
        var published = await ReadHorizonAsync();
        published.Should().BeGreaterThan(committed);
        var resumed = await CreateLog().ReadAsync(
            new FeatureChangeLogQuery(committed, null, null, [layer.LayerId], 10), CancellationToken.None);
        var change = resumed.Should().ContainSingle().Which;
        change.ObjectId.Should().Be(objectId);
        change.Cursor.Should().Be(published);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadHorizon_RolledBackAllocation_DoesNotPublishAnEmptySequenceHead()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734306);
        await layer.InsertAsync("""{"name":"committed"}""");
        var committed = await layer.HeadAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO honua.features (layer_id, attributes)
                VALUES (@layer, '{"name":"rolled back"}'::jsonb)
                """, connection, transaction);
            insert.Parameters.AddWithValue("layer", layer.LayerId);
            await insert.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        await using var sequence = new NpgsqlCommand("SELECT last_value FROM honua.sync_generation", connection);
        ((long)(await sequence.ExecuteScalarAsync())!).Should().BeGreaterThan(committed);
        (await ReadHorizonAsync()).Should().Be(committed);
        (await CreateTracker().GetCurrentGenerationAsync()).Should().Be(committed);

        var objectId = await layer.InsertAsync("""{"name":"after rollback"}""");
        var resumed = await CreateLog().ReadAsync(
            new FeatureChangeLogQuery(committed, null, null, [layer.LayerId], 10), CancellationToken.None);
        resumed.Should().ContainSingle().Which.ObjectId.Should().Be(objectId);
    }

    private PostgresChangeTracker CreateTracker() => new(Connections());

    private PostgresFeatureChangeLog CreateLog() => CreateLog(new RealtimeOptions(), TimeProvider.System);

    private PostgresFeatureChangeLog CreateLog(RealtimeOptions options, TimeProvider time)
        => new(new JobStoreDatabase(database.DataSource), new RealtimeCapabilityLimits(Options.Create(options)), time);

    private static async Task InsertUnpublishedAsync(FeatureChangeTestLayer layer, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await layer.InsertAsync("""{"name":"not yet published"}""");
        }
    }

    private Task PinHorizonAsync(long generation)
        => ExecuteAsync($"""
            CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
            RETURNS BIGINT LANGUAGE sql STABLE AS $$ SELECT {generation}::BIGINT $$;
            """);

    private TestDatabaseConnectionProvider Connections() => new(database.DataSource);

    private async Task<long> ReadHorizonAsync()
    {
        await using var command = database.DataSource.CreateCommand("SELECT honua.feature_change_read_horizon()");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
