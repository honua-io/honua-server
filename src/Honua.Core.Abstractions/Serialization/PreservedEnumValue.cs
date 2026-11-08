// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

public readonly record struct PreservedEnumValue<TEnum> where TEnum : struct, Enum
{
    internal static TEnum UnknownValue { get; } = Enum.Parse<TEnum>("Unknown");

    public TEnum Value { get; }

    internal string? RawToken { get; }

    public PreservedEnumValue(TEnum value)
    {
        Value = value;
    }

    internal PreservedEnumValue(TEnum value, string rawToken)
    {
        Value = value;
        RawToken = rawToken;
    }

    internal static PreservedEnumValue<TEnum> FromUnknownToken(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new PreservedEnumValue<TEnum>(UnknownValue, document.RootElement.GetRawText());
    }

    public static implicit operator PreservedEnumValue<TEnum>(TEnum value) => new(value);
}
