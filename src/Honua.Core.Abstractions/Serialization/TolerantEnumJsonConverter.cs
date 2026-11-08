// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Honua.Core.Serialization;

internal class TolerantEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    internal static JsonSerializerOptions ConverterOptions { get; } = new()
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine()
    };

    private readonly JsonConverter<TEnum> _knownValues;

    public TolerantEnumJsonConverter()
    {
        var factory = new JsonStringEnumConverter<TEnum>();
        _knownValues = (JsonConverter<TEnum>)factory.CreateConverter(typeof(TEnum), ConverterOptions);
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadPreserved(ref reader, options).Value;

    internal virtual PreservedEnumValue<TEnum> ReadPreserved(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var originalReader = reader;
        try
        {
            var value = _knownValues.Read(ref reader, typeof(TEnum), options);
            if (IsRecognizedValue(value))
            {
                return new PreservedEnumValue<TEnum>(value);
            }
        }
        catch (JsonException) when (reader.TokenType == JsonTokenType.String ||
            JsonIntegerToken.IsInteger(ref reader))
        {
        }

        return PreservedEnumValue<TEnum>.FromUnknownToken(ref originalReader);
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => _knownValues.Write(writer, KnownOrUnknown(value), options);

    internal static bool IsRecognizedValue(TEnum value)
        => Enum.IsDefined(value) &&
           (!PreservedEnumValue<TEnum>.UnknownIsNegativeSentinel ||
            !EqualityComparer<TEnum>.Default.Equals(value, PreservedEnumValue<TEnum>.UnknownValue));

    protected static TEnum KnownOrUnknown(TEnum value)
        => Enum.IsDefined(value) ? value : PreservedEnumValue<TEnum>.UnknownValue;
}
