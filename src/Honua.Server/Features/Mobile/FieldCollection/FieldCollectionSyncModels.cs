// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Mobile.FieldCollection;

// Payloads are embedded as raw text so coordinate precision is not re-encoded.
internal sealed class RawJsonElementConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    return document.RootElement.GetRawText();
                }
            default:
                throw new JsonException("FieldCollection feature payload must be a JSON object or array.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(value);
        document.RootElement.WriteTo(writer);
    }
}

internal sealed record FieldCollectionGenerationResponse
{
    [JsonPropertyName("serverGeneration")]
    public required long ServerGeneration { get; init; }
}

internal sealed record FieldCollectionSyncCursorResponse
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("lastSyncGeneration")]
    public required long LastSyncGeneration { get; init; }
}

// The server never advances the cursor during pull; the client advances it after local persistence.
internal sealed record FieldCollectionSyncCursorAckRequest
{
    [JsonPropertyName("lastSyncGeneration")]
    public long? LastSyncGeneration { get; init; }
}

internal sealed record FieldCollectionServerChange
{
    [JsonPropertyName("featureId")]
    public required string FeatureId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }

    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("feature")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public string? Feature { get; init; }
}

internal sealed record FieldCollectionPullResponse
{
    [JsonPropertyName("serverGeneration")]
    public required long ServerGeneration { get; init; }

    [JsonPropertyName("nextCursor")]
    public required long NextCursor { get; init; }

    [JsonPropertyName("hasMore")]
    public required bool HasMore { get; init; }

    [JsonPropertyName("changes")]
    public required FieldCollectionServerChange[] Changes { get; init; }
}

internal sealed record FieldCollectionPushRequestModel
{
    [JsonPropertyName("changeId")]
    public string? ChangeId { get; init; }

    [JsonPropertyName("featureId")]
    public string? FeatureId { get; init; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; init; }

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    [JsonPropertyName("baseVersion")]
    public long? BaseVersion { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("feature")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public string? Feature { get; init; }
}

internal sealed record FieldCollectionPushResponse
{
    [JsonPropertyName("changeId")]
    public required string ChangeId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("serverGeneration")]
    public required long ServerGeneration { get; init; }

    [JsonPropertyName("version")]
    public long? Version { get; init; }

    [JsonPropertyName("conflictType")]
    public string? ConflictType { get; init; }

    [JsonPropertyName("serverVersion")]
    public long? ServerVersion { get; init; }

    [JsonPropertyName("serverFeature")]
    [JsonConverter(typeof(RawJsonElementConverter))]
    public string? ServerFeature { get; init; }

    [JsonPropertyName("rejectionReason")]
    public string? RejectionReason { get; init; }
}
