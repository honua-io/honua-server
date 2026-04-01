// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Source-generated JSON serialization context for feature streaming models (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(FeatureStreamEnvelope))]
[JsonSerializable(typeof(FeatureStreamHeartbeat))]
[JsonSerializable(typeof(ApiResponse<FeatureStreamStatusResponse>))]
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

/// <summary>
/// Heartbeat frame payload sent to keep connections alive.
/// </summary>
internal sealed record FeatureStreamHeartbeat
{
    /// <summary>
    /// Frame type identifier.
    /// </summary>
    public string Type { get; init; } = "heartbeat";

    /// <summary>
    /// Server timestamp of this heartbeat.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Admin status response for feature streaming sessions.
/// </summary>
internal sealed class FeatureStreamStatusResponse
{
    /// <summary>
    /// Total number of active streaming sessions.
    /// </summary>
    public int ActiveSessions { get; init; }

    /// <summary>
    /// Breakdown by transport type.
    /// </summary>
    public int WebSocketSessions { get; init; }

    /// <summary>
    /// SSE session count.
    /// </summary>
    public int SseSessions { get; init; }

    /// <summary>
    /// Total slow-consumer disconnects since startup.
    /// </summary>
    public long SlowConsumerDrops { get; init; }

    /// <summary>
    /// Total heartbeats sent since startup.
    /// </summary>
    public long HeartbeatsSent { get; init; }

    /// <summary>
    /// Per-session details.
    /// </summary>
    public FeatureStreamSessionResponse[] Sessions { get; init; } = [];

    /// <summary>
    /// When this snapshot was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Admin view of a single feature streaming session.
/// </summary>
internal sealed class FeatureStreamSessionResponse
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public Guid SessionId { get; init; }

    /// <summary>
    /// When the client connected.
    /// </summary>
    public DateTimeOffset ConnectedAt { get; init; }

    /// <summary>
    /// Optional label set by the client at connect time.
    /// </summary>
    public string? ClientLabel { get; init; }

    /// <summary>
    /// Transport type (WebSocket or SSE).
    /// </summary>
    public string Transport { get; init; } = string.Empty;

    /// <summary>
    /// Last cursor queued to this session's outbound channel (may not yet be delivered to the client).
    /// </summary>
    public long LastQueuedCursor { get; init; }

    /// <summary>
    /// Connection duration in seconds.
    /// </summary>
    public double DurationSeconds { get; init; }

    /// <summary>
    /// Whether this session has an active subscription filter.
    /// </summary>
    public bool HasFilter { get; init; }

    /// <summary>
    /// Human-readable summary of the subscription filter, if any.
    /// </summary>
    public string? FilterSummary { get; init; }

    /// <summary>
    /// Service ID filter applied to this session, if any.
    /// </summary>
    public string? ServiceIdFilter { get; init; }

    /// <summary>
    /// Layer ID filter applied to this session, if any.
    /// </summary>
    public int[]? LayerIdFilter { get; init; }
}
