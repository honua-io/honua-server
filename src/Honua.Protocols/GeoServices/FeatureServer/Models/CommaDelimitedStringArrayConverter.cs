// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

// supportedQueryFormats is written as a comma-delimited string because the ArcGIS Maps SDK for JavaScript calls split on it and fails FeatureLayer.load when the value is an array; reading still accepts either form.
public sealed class CommaDelimitedStringArrayConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var item = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        values.Add(item);
                    }
                }
            }

            return [.. values];
        }

        throw new JsonException(
            $"Unexpected token {reader.TokenType} when reading a comma-delimited string array.");
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value is { Length: > 0 } ? string.Join(", ", value) : string.Empty);
    }
}
