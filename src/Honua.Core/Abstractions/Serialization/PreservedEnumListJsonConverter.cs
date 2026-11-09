// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Serialization;

internal sealed class PreservedEnumListJsonConverter<TEnum> : JsonConverter<IReadOnlyList<PreservedEnumValue<TEnum>>>
    where TEnum : struct, Enum
{
    private readonly PreservedEnumJsonConverter<TEnum> _values = new();

    public override IReadOnlyList<PreservedEnumValue<TEnum>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Enum values must be an array.");
        }

        var values = new List<PreservedEnumValue<TEnum>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(_values.Read(ref reader, typeof(PreservedEnumValue<TEnum>), options));
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<PreservedEnumValue<TEnum>> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            _values.Write(writer, item, options);
        }
        writer.WriteEndArray();
    }
}
