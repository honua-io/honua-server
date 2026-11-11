// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.SensorThings.Domain;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamTenantIsolationTests
{
    [Theory]
    [InlineData("tenant-b", "schema_a")]
    [InlineData("tenant-a", "schema_b")]
    [InlineData(null, "schema_a")]
    [InlineData("tenant-a", null)]
    [Trait("Tier", "Fast")]
    public void PublishObservations_DifferentScope_DoesNotDeliverEvenWithCollidingIds(string? tenant, string? schema)
    {
        using var manager = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance);
        var scope = new ObservationStreamScope("tenant-a", "schema_a");
        using var own = manager.TryCreateSession("SSE", 1, scope)!;
        using var other = manager.TryCreateSession("SSE", 1, new(tenant, schema))!;
        using var otherAll = manager.TryCreateSession("WebSocket", null, new(tenant, schema))!;
        using var wrongDatastream = manager.TryCreateSession("SSE", 2, scope)!;

        manager.PublishObservations([Observation(42.5)], scope);

        Assert.True(own.Reader.TryRead(out var frame));
        AssertFrame(frame, 42.5);
        Assert.False(other.Reader.TryRead(out _));
        Assert.False(otherAll.Reader.TryRead(out _));
        Assert.False(wrongDatastream.Reader.TryRead(out _));
    }

    [UnitTest]
    public void PublishObservations_UnscopedPartition_DoesNotReachTenantSessions()
    {
        using var manager = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance);
        using var unscoped = manager.TryCreateSession("SSE", null, new(null, null))!;
        using var tenant = manager.TryCreateSession("SSE", 1, new("tenant-a", "schema_a"))!;

        manager.PublishObservations([Observation(81.25)], new ObservationStreamScope(null, null));

        Assert.True(unscoped.Reader.TryRead(out var frame));
        AssertFrame(frame, 81.25);
        Assert.False(tenant.Reader.TryRead(out _));
    }

    [UnitTest]
    public void PublishObservations_TwoNodes_DeliversOnlyToSessionsOnThePublishingNode()
    {
        using var first = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance);
        using var second = new ObservationStreamSessionManager(NullLogger<ObservationStreamSessionManager>.Instance);
        var scope = new ObservationStreamScope("tenant-a", "schema_a");
        using var local = first.TryCreateSession("SSE", 1, scope)!;
        using var remote = second.TryCreateSession("WebSocket", 1, scope)!;

        first.PublishObservations([Observation(42.5)], scope);

        Assert.True(local.Reader.TryRead(out var frame));
        AssertFrame(frame, 42.5);
        Assert.False(remote.Reader.TryRead(out _));
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
