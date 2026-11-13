// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Infrastructure.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Realtime;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Alerts;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class RealtimeAlertDeliveryTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task DeliverAsync_OnOneTask_PersistsEnvelopeForAnotherTask()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var producer = new PostgresRealtimeFanoutProvider(
            harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));
        using var services = new ServiceCollection()
            .AddSingleton<IRealtimeFanoutProvider>(producer)
            .AddSingleton<IAlertNotificationBroadcaster, InMemoryAlertNotificationBroadcaster>()
            .BuildServiceProvider();
        var sink = ActivatorUtilities.CreateInstance<WebSocketAlertDeliverySink>(services);
        var alert = AlertTestFixtures.CreateAlertEvent(dedupeKey: "cross-task-alert");

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket), alert);

        result.Succeeded.Should().BeTrue();
        var events = await harness.Log.ReadAfterAsync(new RealtimeLogRead("alerts", 0, 10), CancellationToken.None);
        var persisted = events.Should().ContainSingle().Subject;
        persisted.ScopeKey.Should().Be(alert.ServiceId);
        persisted.Kind.Should().Be("alert");
        persisted.Payload.GetProperty("dedupeKey").GetString().Should().Be(alert.DedupeKey);
        persisted.Payload.GetProperty("serviceId").GetString().Should().Be(alert.ServiceId);
        persisted.Payload.GetProperty("layerId").GetInt32().Should().Be(alert.LayerId);
    }
}
