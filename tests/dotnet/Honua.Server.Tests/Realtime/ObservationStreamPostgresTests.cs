// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;
using Honua.Protocols.SensorThings.Streaming;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.SensorThings)]
[Operation(Operations.SecurityTesting)]
public sealed class ObservationStreamPostgresTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task TwoTasks_ReplayCommittedBatchesWithExactTenantSchemaAndDatastream()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var dispatcherA = harness.CreateDispatcher();
        var dispatcherB = harness.CreateDispatcher();
        var nodeA = new InProcessRealtimeFanoutProvider(harness.Log, dispatcherA, new RealtimePresenceBatcher(harness.Log));
        var nodeB = new PostgresRealtimeFanoutProvider(harness.Log, dispatcherB, new RealtimePresenceBatcher(harness.Log));
        await nodeA.AppendAsync(Draft("tenant-a", "schema_a", "1"), null, CancellationToken.None);
        await nodeA.AppendAsync(Draft("tenant-b", "schema_b", "1"), null, CancellationToken.None);
        await nodeA.AppendAsync(Draft("tenant-a", "schema_a", "2"), null, CancellationToken.None);
        await nodeA.AppendAsync(Draft("tenant-a", "schema_a", "1"), null, CancellationToken.None);
        await using var first = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance, nodeA);
        await using var second = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance, nodeB);
        await using var local = first.TryCreateSession(new("SSE", 1, new("tenant-a", "schema_a"), "alice", 0), out _)!;
        await using var remote = second.TryCreateSession(new("WebSocket", 1, new("tenant-a", "schema_a"), "alice", 0), out _)!;
        await using var tenantB = second.TryCreateSession(new("SSE", null, new("tenant-b", "schema_b"), "bob", 0), out _)!;
        await using var wrongSchema = second.TryCreateSession(new("SSE", null, new("tenant-a", "schema_b"), "alice", 0), out _)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var session in new[] { local, remote })
        {
            Assert.Equal(1, Assert.IsType<RealtimeDelivery.Event>(await session.Reader.ReadAsync(timeout.Token)).Value.Sequence);
            Assert.Equal(4, Assert.IsType<RealtimeDelivery.Event>(await session.Reader.ReadAsync(timeout.Token)).Value.Sequence);
            Assert.False(session.Reader.TryRead(out _));
        }
        Assert.Equal(2, Assert.IsType<RealtimeDelivery.Event>(await tenantB.Reader.ReadAsync(timeout.Token)).Value.Sequence);
        Assert.False(wrongSchema.Reader.TryRead(out _));
        await using var resumed = second.TryCreateSession(new("SSE", 1, new("tenant-a", "schema_a"), "alice", 1), out _)!;
        Assert.Equal(4, Assert.IsType<RealtimeDelivery.Event>(await resumed.Reader.ReadAsync(timeout.Token)).Value.Sequence);
        Assert.False(resumed.Reader.TryRead(out _));

        // Probe both independent task dispatchers after another task commits a new batch.
        await dispatcherA.InitializeAsync(timeout.Token);
        await dispatcherB.InitializeAsync(timeout.Token);
        await nodeA.AppendAsync(Draft("tenant-a", "schema_a", "1"), null, timeout.Token);
        await dispatcherA.DispatchOnceAsync(timeout.Token);
        await dispatcherB.DispatchOnceAsync(timeout.Token);
        foreach (var session in new[] { local, remote, resumed })
        {
            Assert.Equal(5, Assert.IsType<RealtimeDelivery.Event>(await session.Reader.ReadAsync(timeout.Token)).Value.Sequence);
        }
        Assert.False(tenantB.Reader.TryRead(out _));
        Assert.False(wrongSchema.Reader.TryRead(out _));
    }

    private static RealtimeEventDraft Draft(string? tenant, string? schema, string datastream)
        => new(RealtimeFamily.Observations, datastream, "observation.created",
            RealtimeLogHarness.Payload(new { scope = new { tenantId = tenant, schema }, datastreamId = long.Parse(datastream, System.Globalization.CultureInfo.InvariantCulture), observations = new[] { new { id = 49, result = 42.5 } } }));
}
