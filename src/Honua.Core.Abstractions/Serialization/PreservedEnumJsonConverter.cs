// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Serialization;

internal class PreservedEnumJsonConverter<TEnum> : JsonConverter<PreservedEnum<TEnum>>
    where TEnum : struct, Enum
{
    private readonly TolerantEnumJsonConverter<TEnum> _values = new();

    public override PreservedEnum<TEnum> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var originalReader = reader;
        var value = _values.Read(ref reader, typeof(TEnum), options);
        return PreservedEnum<TEnum>.FromToken(value, ref originalReader);
    }

    public override void Write(Utf8JsonWriter writer, PreservedEnum<TEnum> value, JsonSerializerOptions options)
    {
        if (value.RawToken is { } rawToken)
        {
            writer.WriteRawValue(rawToken);
            return;
        }

        WriteKnownValue(writer, value.Value, options);
    }

    protected virtual void WriteKnownValue(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => _values.Write(writer, value, options);
}

internal sealed class PreservedNumericEnumJsonConverter<TEnum> : PreservedEnumJsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly TolerantNumericEnumJsonConverter<TEnum> _values = new();

    protected override void WriteKnownValue(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => _values.Write(writer, value, options);
}
