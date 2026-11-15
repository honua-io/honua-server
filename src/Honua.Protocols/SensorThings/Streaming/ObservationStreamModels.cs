// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.SensorThings.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.SensorThings.Streaming;

public sealed record ObservationStreamFrame
{
    [JsonIgnore]
    public bool IsHeartbeat { get; init; }

    [JsonPropertyName("@iot.id")]
    public long IotId { get; init; }

    [JsonPropertyName("datastreamId")]
    public long DatastreamId { get; init; }

    [JsonPropertyName("phenomenonTime")]
    public string? PhenomenonTime { get; init; }

    [JsonPropertyName("result")]
    public System.Text.Json.JsonElement Result { get; init; }

    public static ObservationStreamFrame FromObservation(SensorThingsObservation observation)
    {
        var start = Services.StaEntityMapper.FormatInstant(observation.PhenomenonTime);
        return new()
        {
            IsHeartbeat = false,
            IotId = observation.Id,
            DatastreamId = observation.DatastreamId,
            PhenomenonTime = observation.PhenomenonTimeEnd is { } end
                ? $"{start}/{Services.StaEntityMapper.FormatInstant(end)}"
                : start,
            Result = Services.StaEntityMapper.ResultValue(observation)
        };
    }

    public static ObservationStreamFrame Heartbeat() => new() { IsHeartbeat = true };
}

public sealed record ObservationStreamStatus
{
    public const string ResumableSemantics = "resumable";

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("datastreamId")]
    public long? DatastreamId { get; init; }

    [JsonPropertyName("semantics")]
    public string? Semantics { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    public static ObservationStreamStatus Connected(long? datastreamId) => new()
    {
        Status = "connected",
        DatastreamId = datastreamId,
        Semantics = ResumableSemantics,
    };

    public static ObservationStreamStatus SlowConsumerGap(long? datastreamId) => new()
    {
        Status = "gap",
        DatastreamId = datastreamId,
        Reason = "slow-consumer",
        Semantics = ResumableSemantics,
    };
}

internal sealed record ObservationStreamBatch(
    [property: JsonPropertyName("cursor")] string Cursor,
    [property: JsonPropertyName("datastreamId")] long DatastreamId,
    [property: JsonPropertyName("observations")] System.Text.Json.JsonElement Observations);

internal sealed record ObservationStreamReset(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("cursor")] string Cursor);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ObservationStreamFrame))]
[JsonSerializable(typeof(ObservationStreamStatus))]
[JsonSerializable(typeof(ObservationStreamBatch))]
[JsonSerializable(typeof(ObservationStreamReset))]
internal sealed partial class ObservationStreamJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

internal static partial class ObservationStreamLog
{
    [LoggerMessage(EventId = 5100, Level = LogLevel.Debug, Message = "Observation stream session {SessionId} created ({Transport}, datastream {DatastreamId}).")]
    public static partial void SessionCreated(ILogger logger, Guid sessionId, string transport, long? datastreamId);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Debug, Message = "Observation stream session refused ({Transport}): {Limit} admission cap reached.")]
    public static partial void SessionRejected(ILogger logger, string transport, ObservationStreamAdmissionLimit limit);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Warning, Message = "Observation stream session {SessionId} disconnected because its buffer overflowed.")]
    public static partial void SlowConsumerDisconnected(ILogger logger, Guid sessionId);
}
