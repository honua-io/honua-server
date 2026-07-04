// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization;
using Honua.Core.Features.SensorThings.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// A single frame pushed to an observation-stream client. Either a heartbeat (keep-alive)
/// or a newly-ingested observation rendered in a compact, STA-shaped form.
/// </summary>
public sealed record ObservationStreamFrame
{
    /// <summary>True when this frame is a keep-alive heartbeat rather than an observation.</summary>
    [JsonIgnore]
    public bool IsHeartbeat { get; init; }

    /// <summary>Observation identifier (<c>@iot.id</c>).</summary>
    [JsonPropertyName("@iot.id")]
    public long IotId { get; init; }

    /// <summary>Identifier of the owning datastream.</summary>
    [JsonPropertyName("datastreamId")]
    public long DatastreamId { get; init; }

    /// <summary>Phenomenon time (ISO-8601).</summary>
    [JsonPropertyName("phenomenonTime")]
    public string? PhenomenonTime { get; init; }

    /// <summary>The numeric measurement result.</summary>
    [JsonPropertyName("result")]
    public double Result { get; init; }

    /// <summary>Builds a frame from a persisted observation.</summary>
    public static ObservationStreamFrame FromObservation(SensorThingsObservation observation) => new()
    {
        IsHeartbeat = false,
        IotId = observation.Id,
        DatastreamId = observation.DatastreamId,
        PhenomenonTime = observation.PhenomenonTime
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        Result = observation.Result
    };

    /// <summary>Builds a keep-alive heartbeat frame.</summary>
    public static ObservationStreamFrame Heartbeat() => new() { IsHeartbeat = true };
}

/// <summary>Status frame emitted on the SSE/WebSocket handshake.</summary>
public sealed record ObservationStreamStatus
{
    /// <summary>Connection status token (e.g. <c>connected</c>).</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>The datastream id this stream is scoped to, or null for all.</summary>
    [JsonPropertyName("datastreamId")]
    public long? DatastreamId { get; init; }
}

/// <summary>Redis pub/sub envelope used to fan observation frames out across nodes.</summary>
internal sealed record ClusterBroadcastDto(string OriginInstanceId, ObservationStreamFrame Frame);

/// <summary>Source-generated JSON context for observation-stream frames.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ObservationStreamFrame))]
[JsonSerializable(typeof(ObservationStreamStatus))]
[JsonSerializable(typeof(ClusterBroadcastDto))]
internal sealed partial class ObservationStreamJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>Source-generated log messages for the observation-stream transport.</summary>
internal static partial class ObservationStreamLog
{
    [LoggerMessage(EventId = 5100, Level = LogLevel.Debug, Message = "Observation stream session {SessionId} created ({Transport}, datastream {DatastreamId}).")]
    public static partial void SessionCreated(ILogger logger, Guid sessionId, string transport, long? datastreamId);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Observation stream cluster fan-out unavailable; degraded to single-node delivery.")]
    public static partial void ClusterUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning, Message = "Observation stream cluster publish failed.")]
    public static partial void ClusterPublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Observation stream cluster unsubscribe failed during dispose; continuing shutdown.")]
    public static partial void ClusterUnsubscribeFailed(ILogger logger, Exception exception);
}
