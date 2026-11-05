// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Streaming;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(FeatureStreamEnvelope))]
[JsonSerializable(typeof(FeatureStreamHeartbeat))]
[JsonSerializable(typeof(FeatureStreamControlMessage))]
[JsonSerializable(typeof(FeatureStreamStatusFrame))]
[JsonSerializable(typeof(FeatureStreamErrorFrame))]
[JsonSerializable(typeof(FeatureStreamSnapshotBeginFrame))]
[JsonSerializable(typeof(FeatureStreamSnapshotFeatureFrame))]
[JsonSerializable(typeof(FeatureStreamSnapshotEndFrame))]
[JsonSerializable(typeof(FeatureStreamSnapshotFrame))]
[JsonSerializable(typeof(FeatureStreamSnapshotFeature))]
[JsonSerializable(typeof(FeatureStreamSnapshotFeature[]))]
[JsonSerializable(typeof(FeatureStreamSnapshotGeoJsonFeature))]
[JsonSerializable(typeof(FeatureStreamConformanceCapability))]
[JsonSerializable(typeof(FeatureStreamCapabilitiesResponse))]
[JsonSerializable(typeof(FeatureStreamLayerCapability))]
[JsonSerializable(typeof(FeatureStreamLayerCapability[]))]
[JsonSerializable(typeof(FeatureStreamBroadcastMessage))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamCapabilitiesResponse>))]
[JsonSerializable(typeof(FeatureStreamStatusResponse))]
[JsonSerializable(typeof(FeatureStreamSessionResponse))]
[JsonSerializable(typeof(FeatureStreamSessionResponse[]))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class FeatureStreamJsonContext : JsonSerializerContext
{
}

internal sealed record FeatureStreamHeartbeat
{
    public string Type { get; init; } = "heartbeat";

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class FeatureStreamStatusResponse
{
    public int ActiveSessions { get; init; }

    public int WebSocketSessions { get; init; }

    public int SseSessions { get; init; }

    public long SlowConsumerDrops { get; init; }

    public long HeartbeatsSent { get; init; }

    public FeatureStreamSessionResponse[] Sessions { get; init; } = [];

    public DateTimeOffset GeneratedAt { get; init; }
}

internal sealed class FeatureStreamSessionResponse
{
    public Guid SessionId { get; init; }

    public DateTimeOffset ConnectedAt { get; init; }

    public string? ClientLabel { get; init; }

    public string Transport { get; init; } = string.Empty;

    public long LastQueuedCursor { get; init; }

    public double DurationSeconds { get; init; }

    public bool HasFilter { get; init; }

    public string? FilterSummary { get; init; }

    public string? ServiceIdFilter { get; init; }

    public int[]? LayerIdFilter { get; init; }
}
