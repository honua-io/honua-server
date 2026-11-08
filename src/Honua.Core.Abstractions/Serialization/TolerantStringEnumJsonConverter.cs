// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Serialization;

internal class TolerantStringEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly TEnum UnknownValue = Enum.Parse<TEnum>("Unknown");
    private readonly JsonConverter<TEnum> _knownValues;

    public TolerantStringEnumJsonConverter()
    {
        var factory = new JsonStringEnumConverter<TEnum>();
        _knownValues = (JsonConverter<TEnum>)factory.CreateConverter(typeof(TEnum), JsonSerializerOptions.Default);
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return KnownOrUnknown(_knownValues.Read(ref reader, typeToConvert, options));
        }
        catch (JsonException) when (reader.TokenType == JsonTokenType.String ||
            JsonIntegerToken.IsInteger(ref reader))
        {
            return UnknownValue;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => _knownValues.Write(writer, KnownOrUnknown(value), options);

    protected static TEnum KnownOrUnknown(TEnum value)
        => Enum.IsDefined(value) ? value : UnknownValue;
}
