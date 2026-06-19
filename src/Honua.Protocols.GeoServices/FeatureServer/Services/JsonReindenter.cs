// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Transcodes a compact UTF-8 JSON payload into an indented (pretty-printed) UTF-8 JSON
/// payload. Used to honor the GeoServices <c>f=pjson</c> format, which is JSON with
/// indentation, without maintaining a parallel indented source-generated serializer
/// context (the transform is type-agnostic and AOT-safe — it reads tokens and re-emits
/// them with <see cref="JsonWriterOptions.Indented"/> set).
/// </summary>
internal static class JsonReindenter
{
    /// <summary>
    /// Re-emits <paramref name="compactUtf8Json"/> with indentation. The input must be a
    /// single well-formed JSON document (which it always is here — it was just produced by
    /// the source-generated serializer).
    /// </summary>
    public static byte[] ToIndentedUtf8Bytes(ReadOnlySpan<byte> compactUtf8Json)
    {
        var buffer = new ArrayBufferWriter<byte>(compactUtf8Json.Length + (compactUtf8Json.Length / 2) + 16);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            var reader = new Utf8JsonReader(compactUtf8Json);
            while (reader.Read())
            {
                WriteToken(ref reader, writer);
            }
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteToken(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                break;
            case JsonTokenType.EndObject:
                writer.WriteEndObject();
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                break;
            case JsonTokenType.EndArray:
                writer.WriteEndArray();
                break;
            case JsonTokenType.PropertyName:
                writer.WritePropertyName(reader.GetString()!);
                break;
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.Number:
                // Preserve the exact numeric token text (no float round-tripping).
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
        }
    }
}
