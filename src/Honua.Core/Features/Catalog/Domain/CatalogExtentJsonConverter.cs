// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Catalog.Domain;

public sealed class CatalogExtentJsonConverter : JsonConverter<MetadataV2Bbox>
{
    public override MetadataV2Bbox Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected four extent coordinates.");
        var coordinates = new double[4];
        for (var index = 0; index < coordinates.Length; index++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out coordinates[index]) ||
                !double.IsFinite(coordinates[index])) throw new JsonException("Expected finite extent coordinates.");
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected four extent coordinates.");
        return new() { West = coordinates[0], South = coordinates[1], East = coordinates[2], North = coordinates[3] };
    }

    public override void Write(Utf8JsonWriter writer, MetadataV2Bbox value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.West);
        writer.WriteNumberValue(value.South);
        writer.WriteNumberValue(value.East);
        writer.WriteNumberValue(value.North);
        writer.WriteEndArray();
    }
}
