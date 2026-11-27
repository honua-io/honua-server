// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Rendering;

internal sealed class MapLibreExpressionConverter : JsonConverter<MapLibreExpression>
{
    public override MapLibreExpression Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => new MapLibreExpression(reader.GetString() ?? string.Empty),
            JsonTokenType.Number => new MapLibreExpression(reader.GetDouble()),
            JsonTokenType.True => new MapLibreExpression(true),
            JsonTokenType.False => new MapLibreExpression(false),
            JsonTokenType.Null => MapLibreExpression.Null,
            JsonTokenType.StartArray => ReadArray(ref reader, options),
            JsonTokenType.StartObject => SkipObject(ref reader),
            _ => MapLibreExpression.Null
        };
    }

    public override void Write(Utf8JsonWriter writer, MapLibreExpression value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case MapLibreExpressionKind.String:
                if (value.StringValue is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStringValue(value.StringValue);
                }

                break;
            case MapLibreExpressionKind.Number:
                writer.WriteNumberValue(value.NumberValue);
                break;
            case MapLibreExpressionKind.Boolean:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case MapLibreExpressionKind.Array:
                writer.WriteStartArray();
                if (value.Items is { Length: > 0 })
                {
                    foreach (var item in value.Items)
                    {
                        Write(writer, item, options);
                    }
                }

                writer.WriteEndArray();
                break;
            case MapLibreExpressionKind.Null:
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private MapLibreExpression ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var items = new List<MapLibreExpression>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return items.Count > 0
                    ? new MapLibreExpression(items.ToArray())
                    : new MapLibreExpression(Array.Empty<MapLibreExpression>());
            }

            items.Add(Read(ref reader, typeof(MapLibreExpression), options));
        }

        throw new JsonException("Unexpected end of JSON while reading MapLibre expression array.");
    }

    private static MapLibreExpression SkipObject(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return MapLibreExpression.Null;
    }
}
