// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Realtime;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class FeatureStreamPublisherChangeLogTests(FeatureChangeDatabaseFixture database) : IClassFixture<FeatureChangeDatabaseFixture>
{
    private const int LayerId = 734301;

    [IntegrationTest(Timeout = 120000)]
    public async Task PublishStrictAsync_ChangeLoggedUnderTheEventId_DispatchesTheLoggedChange()
    {
        var eventId = Guid.NewGuid().ToString("N");
        var generation = await InsertChangeAsync(eventId);
        var sink = new SignalingSink();
        var publisher = new FeatureStreamPublisher(
            new NoWakeSignal(),
            new PostgresFeatureChangeLog(new JobStoreDatabase(database.DataSource), Options.Create(new RealtimeOptions()), TimeProvider.System),
            NullLogger<FeatureStreamPublisher>.Instance,
            new FeatureChangeEventSinkBroadcaster(
                [sink],
                Options.Create(new FeatureChangeEventSinkOptions { Enabled = true }),
                NullLogger<FeatureChangeEventSinkBroadcaster>.Instance));

        try
        {
            await publisher.PublishStrictAsync(new FeatureChangeEventRequest
            {
                EventId = eventId,
                ServiceId = "svc-logged",
                LayerId = LayerId,
                ObjectId = 7,
                Operation = "update",
                Protocol = "rest",
                RequestId = "req-logged"
            });

            var received = await sink.Signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(generation, received.Cursor);
            Assert.Equal(eventId, received.EventId);
            Assert.Equal(LayerId, received.LayerId);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM honua.feature_changes WHERE event_id = @event_id", eventId);
        }
    }

    private async Task<long> InsertChangeAsync(string eventId)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, changed_at, event_id)
            VALUES (nextval('honua.sync_generation'), @layer, 7, 2, now(), @event_id)
            RETURNING generation
            """,
            connection);
        command.Parameters.AddWithValue("layer", LayerId);
        command.Parameters.AddWithValue("event_id", eventId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, string eventId)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NoWakeSignal : IRealtimeWakeSignal
    {
        public void Wake()
        {
        }
    }

    private sealed class SignalingSink : IFeatureChangeEventSink
    {
        public TaskCompletionSource<FeatureChangeEvent> Signal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "signaling";

        public Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
        {
            Signal.TrySetResult(featureEvent);
            return Task.CompletedTask;
        }
    }
}
