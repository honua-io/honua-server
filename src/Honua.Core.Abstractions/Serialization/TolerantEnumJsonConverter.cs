// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Serialization;

internal class TolerantEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly JsonConverter<TEnum> _knownValues;

    public TolerantEnumJsonConverter()
    {
        var factory = new JsonStringEnumConverter<TEnum>();
        _knownValues = (JsonConverter<TEnum>)factory.CreateConverter(typeof(TEnum), JsonSerializerOptions.Default);
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadPreserved(ref reader, options).Value;

    internal virtual PreservedEnum<TEnum> ReadPreserved(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var originalReader = reader;
        try
        {
            var value = _knownValues.Read(ref reader, typeof(TEnum), options);
            if (Enum.IsDefined(value))
            {
                return new PreservedEnum<TEnum>(value);
            }
        }
        catch (JsonException) when (reader.TokenType == JsonTokenType.String ||
            JsonIntegerToken.IsInteger(ref reader))
        {
        }

        return PreservedEnum<TEnum>.FromUnknownToken(ref originalReader);
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => _knownValues.Write(writer, KnownOrUnknown(value), options);

    protected static TEnum KnownOrUnknown(TEnum value)
        => Enum.IsDefined(value) ? value : PreservedEnum<TEnum>.UnknownValue;
}
