// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Mobile.FieldCollection;

/// <summary>
/// Reads and writes pre-serialized JSON object payloads as raw text without
/// re-quoting them as JSON strings. Used to embed the mobile-provided feature
/// payload verbatim across pull and push paths, preserving CRS, datum, and
/// coordinate precision exactly as stored.
/// </summary>
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

/// <summary>
/// Response shape for <c>GET /api/v1/fieldcollection/generation</c>. Returns the
/// current monotonic server generation cursor.
/// </summary>
internal sealed record FieldCollectionGenerationResponse
{
    [JsonPropertyName("serverGeneration")]
    public required long ServerGeneration { get; init; }
}

/// <summary>
/// Response shape for <c>GET /api/v1/fieldcollection/sync-cursor</c> and
/// <c>POST /api/v1/fieldcollection/sync-cursor</c>.
/// </summary>
internal sealed record FieldCollectionSyncCursorResponse
{
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }

    [JsonPropertyName("lastSyncGeneration")]
    public required long LastSyncGeneration { get; init; }
}

/// <summary>
/// Request shape for <c>POST /api/v1/fieldcollection/sync-cursor</c>. Clients
/// call this after local persistence succeeds to advance the server's
/// per-client cursor; the server never advances it implicitly during pull.
/// </summary>
internal sealed record FieldCollectionSyncCursorAckRequest
{
    [JsonPropertyName("lastSyncGeneration")]
    public long? LastSyncGeneration { get; init; }
}

/// <summary>
/// Single change entry returned by the pull endpoint. <see cref="Feature"/> is
/// the pre-serialized GeoJSON-style payload for inserts and updates and null
/// for deletes.
/// </summary>
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

/// <summary>
/// Response shape for <c>GET /api/v1/fieldcollection/changes</c>.
/// </summary>
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

/// <summary>
/// Request shape for <c>POST /api/v1/fieldcollection/changes</c>. Each request
/// represents a single mobile-pushed change. The bounded batch case is a
/// future extension; the mobile <c>IFieldCollectionChangeUploader</c> sends
/// one change at a time today.
/// </summary>
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

/// <summary>
/// Response shape for <c>POST /api/v1/fieldcollection/changes</c>. The server
/// always responds with HTTP 200 for explicit machine-readable outcomes
/// (applied, conflict, rejected). Validation and unauthorized cases return
/// the standard problem-details envelope at HTTP 400 / 401.
/// </summary>
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
