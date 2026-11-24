// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

// Drives the SSE replay path directly, from cursor 0, for one subscription filter.
internal static class FeatureStreamReplayHarness
{
    private static readonly MethodInfo ReplayToSse = typeof(FeatureStreamEndpoints).GetMethod(
        "ReplayToSseAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    internal sealed record Replay(HttpResponse Response, IFeatureChangeEventStore Store, StreamSubscriptionFilter Filter);

    internal static FeatureStreamSessionManager CreateManager() => new(
        Options.Create(new FeatureStreamOptions()),
        NullLogger<FeatureStreamSessionManager>.Instance,
        TestTelemetry.CreateFeatureStreamMetrics());

    internal static async Task<long> ReplayAsync(Replay replay, CancellationToken cancellationToken)
    {
        using var manager = CreateManager();
        return await (Task<long>)ReplayToSse.Invoke(null,
            [replay.Response, replay.Store, 0L, 100, NullLogger.Instance, Guid.NewGuid(), manager,
             cancellationToken, replay.Filter, null, 0L, null])!;
    }

    internal static FakeFeatureChangeEventStore CreateStoreWithOneChange(string? propertiesJson = null)
    {
        var store = new FakeFeatureChangeEventStore();
        store.Append(new FeatureChangeEventRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "test",
            RequestId = "request",
            PropertiesJson = propertiesJson
        });
        return store;
    }
}
