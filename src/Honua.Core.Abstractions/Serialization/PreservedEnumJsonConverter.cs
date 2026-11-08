// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Serialization;

internal class PreservedEnumJsonConverter<TEnum> : JsonConverter<PreservedEnumValue<TEnum>>
    where TEnum : struct, Enum
{
    internal static PreservedEnumJsonConverter<TEnum> Instance { get; } = new();

    private readonly TolerantEnumJsonConverter<TEnum> _values;

    public PreservedEnumJsonConverter() : this(new TolerantEnumJsonConverter<TEnum>())
    {
    }

    protected PreservedEnumJsonConverter(TolerantEnumJsonConverter<TEnum> values)
    {
        _values = values;
    }

    public override PreservedEnumValue<TEnum> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => _values.ReadPreserved(ref reader, options);

    public override void Write(Utf8JsonWriter writer, PreservedEnumValue<TEnum> value, JsonSerializerOptions options)
    {
        if (value.RawToken is { } rawToken)
        {
            writer.WriteRawValue(rawToken);
            return;
        }

        _values.Write(writer, value.Value, options);
    }
}

internal sealed class PreservedNumericEnumJsonConverter<TEnum> : PreservedEnumJsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public PreservedNumericEnumJsonConverter() : base(new TolerantNumericEnumJsonConverter<TEnum>())
    {
    }
}
