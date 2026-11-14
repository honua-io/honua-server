// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamTenantIsolationTests
{
    [Theory]
    [InlineData("tenant-b", "schema_a")]
    [InlineData("tenant-a", "schema_b")]
    [InlineData(null, "schema_a")]
    [InlineData("tenant-a", null)]
    [Trait("Tier", "Fast")]
    public async Task Subscribe_DifferentScope_DoesNotMatchEvenWithCollidingIds(string? tenant, string? schema)
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        RealtimeSubscriptionRequest? request = null;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value => request = value))
            .Returns(Substitute.For<IRealtimeSubscription>());
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        await using var session = manager.TryCreateSession(new ObservationStreamRequest(
            "SSE", 1, new("tenant-a", "schema_a"), "alice", 0), out _);
        Assert.NotNull(request);
        Assert.False(request.Filter!(new RealtimeEvent(RealtimeFamily.Observations, 1, "1",
            "observation.created", JsonSerializer.SerializeToElement(new { scope = new { tenantId = tenant, schema } }),
            DateTimeOffset.UtcNow)));
        // Missing scope must not act as the single-tenant partition or as a wildcard.
        Assert.False(request.Filter(new RealtimeEvent(RealtimeFamily.Observations, 2, "1",
            "observation.created", JsonSerializer.SerializeToElement(new { datastreamId = 1 }),
            DateTimeOffset.UtcNow)));
    }

    [UnitTest]
    public void FromObservation_IncludesPhenomenonTimeIntervalEnd()
    {
        var end = DateTimeOffset.Parse("2026-09-05T02:03:04Z", System.Globalization.CultureInfo.InvariantCulture);
        var frame = ObservationStreamFrame.FromObservation(Observation(1.25) with { PhenomenonTimeEnd = end });
        Assert.Equal("2026-09-05T01:02:03.000Z/2026-09-05T02:03:04.000Z", frame.PhenomenonTime);
        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
        Assert.Contains("2026-09-05T02:03:04.000Z", json, StringComparison.Ordinal);
    }

    internal static SensorThingsObservation Observation(double value) => new()
    {
        Id = 49,
        DatastreamId = 1,
        PhenomenonTime = DateTimeOffset.Parse("2026-09-05T01:02:03Z", System.Globalization.CultureInfo.InvariantCulture),
        Result = value
    };

    internal static void AssertFrame(ObservationStreamFrame frame, double expected)
    {
        Assert.Equal(49, frame.IotId);
        Assert.Equal(1, frame.DatastreamId);
        Assert.Equal(expected, frame.Result.GetDouble());
        Assert.Equal("2026-09-05T01:02:03.000Z", frame.PhenomenonTime);
        // Internal routing information is never part of the public observation payload.
        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
        Assert.DoesNotContain("Tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Schema", json, StringComparison.OrdinalIgnoreCase);
    }
}
