// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.SpatialAnalytics.Models;

internal sealed record SpatialAnalyticsGeometry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("coordinates")]
    [JsonConverter(typeof(SpatialAnalyticsRawJsonConverter))]
    public string? CoordinatesJson { get; init; }
}

internal sealed class SpatialAnalyticsRawJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(value);
    }
}
