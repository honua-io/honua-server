// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Realtime;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class WebSocketAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_AppendFailure_IsRetryable()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        provider.AppendAsync(default!, null, default).ReturnsForAnyArgs(ValueTask.FromException<RealtimePosition>(new InvalidOperationException("unavailable")));
#pragma warning restore CA2012
        await using var broadcaster = new RealtimeAlertNotificationBroadcaster(
            provider, NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        var sink = new WebSocketAlertDeliverySink(broadcaster);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket), AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Substitute.For<IRealtimeFanoutProvider>();
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        provider.AppendAsync(default!, null, default).ReturnsForAnyArgs(ValueTask.FromCanceled<RealtimePosition>(cancellation.Token));
#pragma warning restore CA2012
        await using var broadcaster = new RealtimeAlertNotificationBroadcaster(
            provider, NullLogger<RealtimeAlertNotificationBroadcaster>.Instance);
        var sink = new WebSocketAlertDeliverySink(broadcaster);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.WebSocket),
            AlertTestFixtures.CreateAlertEvent(), cancellation.Token));
    }
}
