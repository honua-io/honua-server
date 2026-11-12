// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// honua.feature_changes read as the features family: changes made by any session, including plain
// SQL, are events with their generation as the position. The table is shared with other tests, so
// every assertion is scoped to this test's layer, or bounded so that other writers cannot break it.
public sealed class FeatureChangeLogTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _deliveryTimeout = TimeSpan.FromSeconds(10);

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadAsync_DirectSqlEdits_AreEventsWithGenerationCursorsAndDatabaseProtocol()
    {
        await using var layer = await TestLayer.CreateAsync(database, 734201);
        var before = await layer.HeadAsync();
        var objectId = await layer.InsertAsync("""{"name":"a","count":1}""");
        await layer.UpdateAsync(objectId, """{"name":"a","count":2}""");
        await layer.DeleteAsync(objectId);

        var changes = await CreateLog().ReadAsync(
            new FeatureChangeLogQuery(before, null, null, [layer.LayerId], 10),
            CancellationToken.None);

        changes.Select(change => change.Operation).Should().Equal("insert", "update", "delete");
        changes.Select(change => change.Cursor).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        changes.Should().AllSatisfy(change =>
        {
            change.Protocol.Should().Be(PostgresFeatureChangeLog.DatabaseProtocol);
            change.ServiceId.Should().BeEmpty();
            change.EventId.Should().Be(FeatureStreamCursor.Format(change.Cursor));
            change.ObjectId.Should().Be(objectId);
        });
        changes[0].GeometryJson.Should().NotBeNull();
        changes[0].GeometrySrid.Should().Be(4326);
        changes[0].GeometryChanged.Should().BeTrue();
        changes[1].ChangedAttributes.Should().ContainKey("count").And.NotContainKey("name");
        changes[1].GeometryChanged.Should().BeFalse();
        changes[2].ChangedAttributes.Should().BeNull();
        changes[2].PropertiesJson.Should().Contain("\"count\": 2");
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadAsync_BranchRows_AreNotEvents()
    {
        await using var layer = await TestLayer.CreateAsync(database, 734202);
        var before = await layer.HeadAsync();
        await layer.InsertBranchChangeAsync();
        var objectId = await layer.InsertAsync("""{"name":"default"}""");

        var changes = await CreateLog().ReadAsync(
            new FeatureChangeLogQuery(before, null, null, [layer.LayerId], 10),
            CancellationToken.None);

        changes.Should().ContainSingle().Which.ObjectId.Should().Be(objectId);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadWindowAsync_CountLimit_MovesTheFirstResumablePositionPastOlderChanges()
    {
        await using var layer = await TestLayer.CreateAsync(database, 734203);
        for (var i = 0; i < 4; i++)
        {
            await layer.InsertAsync("""{"n":1}""");
        }

        var generations = await layer.GenerationsAsync();
        var bounded = await CreateLog(new RealtimeOptions { ResumeMaxEvents = 2 }).ReadWindowAsync(CancellationToken.None);
        var unbounded = await CreateLog(new RealtimeOptions { ResumeMaxEvents = 1_000_000 }).ReadWindowAsync(CancellationToken.None);

        // Other writers only add newer changes, which can move the bounded window later, never earlier.
        bounded.Oldest.Should().BeGreaterThan(generations[1]);
        unbounded.Oldest.Should().BeLessThanOrEqualTo(generations[0]);
        unbounded.Head.Should().BeGreaterThanOrEqualTo(generations[^1]);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Dispatcher_FeaturesFamily_DeliversLiveAndResumesAfterAGeneration()
    {
        await using var layer = await TestLayer.CreateAsync(database, 734204);
        var log = CreateLog();
        var dispatcher = new RealtimeDispatcher(
            new PostgresRealtimeEventLog(new JobStoreDatabase(database.DataSource)),
            [log],
            Options.Create(new RealtimeOptions()),
            NullLogger<RealtimeDispatcher>.Instance);
        await dispatcher.InitializeAsync(CancellationToken.None);

        var live = Channel.CreateUnbounded<RealtimeDelivery>();
        await using var liveSubscription = dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Features, [layer.Scope], null, live.Writer));
        await WaitForAsync(() => ((RealtimeSubscription)liveSubscription).IsLive);
        var first = await layer.InsertAsync("""{"n":1}""");
        var second = await layer.InsertAsync("""{"n":2}""");
        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        var delivered = await ReadEventsAsync(live.Reader, 2);
        delivered.Select(ObjectIdOf).Should().Equal(first, second);

        var resumed = Channel.CreateUnbounded<RealtimeDelivery>();
        await using var resumeSubscription = dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Features, [layer.Scope], delivered[0].Sequence, resumed.Writer));
        (await ReadEventsAsync(resumed.Reader, 1)).Select(ObjectIdOf).Should().Equal(second);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task FeatureChangeTrigger_Insert_NotifiesTheFeaturesChannel()
    {
        await using var layer = await TestLayer.CreateAsync(database, 734205);
        await using var listener = await database.DataSource.OpenConnectionAsync();
        var notified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, notification) => notified.TrySetResult(notification.Channel);
        await using (var listen = new NpgsqlCommand("LISTEN honua_rt_features", listener))
        {
            await listen.ExecuteNonQueryAsync();
        }

        await layer.InsertAsync("""{"n":1}""");
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        while (!notified.Task.IsCompleted)
        {
            await listener.WaitAsync(timeout.Token);
        }

        (await notified.Task).Should().Be("honua_rt_features");
    }

    private PostgresFeatureChangeLog CreateLog(RealtimeOptions? options = null)
        => new(new JobStoreDatabase(database.DataSource), Options.Create(options ?? new RealtimeOptions()), TimeProvider.System);

    private static long ObjectIdOf(RealtimeEvent realtimeEvent)
        => realtimeEvent.Payload.GetProperty("objectId").GetInt64();

    private static async Task<IReadOnlyList<RealtimeEvent>> ReadEventsAsync(ChannelReader<RealtimeDelivery> reader, int count)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        var events = new List<RealtimeEvent>();
        while (events.Count < count)
        {
            if (await reader.ReadAsync(timeout.Token) is RealtimeDelivery.Event { Value: var realtimeEvent })
            {
                events.Add(realtimeEvent);
            }
        }

        return events;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    // One layer of honua.features with the change-tracking trigger, removed with its changes on dispose.
    private sealed class TestLayer(DatabaseFixtureAdapter database, int layerId) : IAsyncDisposable
    {
        public int LayerId => layerId;

        public string Scope => layerId.ToString(CultureInfo.InvariantCulture);

        public static async Task<TestLayer> CreateAsync(DatabaseFixtureAdapter database, int layerId)
        {
            var layer = new TestLayer(database, layerId);
            await layer.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS honua.features (
                    objectid BIGSERIAL PRIMARY KEY,
                    layer_id INT NOT NULL,
                    geometry GEOMETRY,
                    attributes JSONB,
                    created_at TIMESTAMPTZ DEFAULT NOW(),
                    updated_at TIMESTAMPTZ DEFAULT NOW());
                DROP TRIGGER IF EXISTS trigger_track_feature_changes ON honua.features;
                CREATE TRIGGER trigger_track_feature_changes
                    AFTER INSERT OR UPDATE OR DELETE ON honua.features
                    FOR EACH ROW EXECUTE FUNCTION honua.track_feature_changes();
                """);
            await layer.CleanupAsync();
            return layer;
        }

        public Task<long> HeadAsync()
            => ScalarAsync<long>("SELECT COALESCE(max(generation), 0) FROM honua.feature_changes");

        public Task<long> InsertAsync(string attributes)
            => ScalarAsync<long>(
                """
                INSERT INTO honua.features (layer_id, geometry, attributes)
                VALUES (@layer, ST_SetSRID(ST_MakePoint(1, 2), 4326), @attributes::jsonb)
                RETURNING objectid
                """,
                ("attributes", attributes));

        public Task UpdateAsync(long objectId, string attributes)
            => ExecuteAsync(
                "UPDATE honua.features SET attributes = @attributes::jsonb WHERE objectid = @object AND layer_id = @layer",
                ("attributes", attributes),
                ("object", objectId));

        public Task DeleteAsync(long objectId)
            => ExecuteAsync("DELETE FROM honua.features WHERE objectid = @object AND layer_id = @layer", ("object", objectId));

        public Task InsertBranchChangeAsync()
            => ExecuteAsync("""
                INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, changed_at, version_id)
                VALUES (nextval('honua.sync_generation'), @layer, 1, 2, now(), gen_random_uuid())
                """);

        public async Task<IReadOnlyList<long>> GenerationsAsync()
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT generation FROM honua.feature_changes WHERE layer_id = @layer AND version_id IS NULL ORDER BY generation",
                connection);
            command.Parameters.AddWithValue("layer", layerId);
            await using var reader = await command.ExecuteReaderAsync();
            var generations = new List<long>();
            while (await reader.ReadAsync())
            {
                generations.Add(reader.GetInt64(0));
            }

            return generations;
        }

        public async ValueTask DisposeAsync() => await CleanupAsync();

        private Task CleanupAsync()
            => ExecuteAsync("""
                DELETE FROM honua.features WHERE layer_id = @layer;
                DELETE FROM honua.feature_changes WHERE layer_id = @layer;
                """);

        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var command = Command(sql, connection, parameters);
            _ = await command.ExecuteNonQueryAsync();
        }

        private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await database.DataSource.OpenConnectionAsync();
            await using var command = Command(sql, connection, parameters);
            return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
        }

        private NpgsqlCommand Command(string sql, NpgsqlConnection connection, (string Name, object Value)[] parameters)
        {
            var command = new NpgsqlCommand(sql, connection);
            if (sql.Contains("@layer", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("layer", layerId);
            }

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return command;
        }
    }
}
