// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

public readonly record struct PreservedEnum<TEnum> where TEnum : struct, Enum
{
    internal static TEnum UnknownValue { get; } = Enum.Parse<TEnum>("Unknown");

    public TEnum Value { get; }

    internal string? RawToken { get; }

    public PreservedEnum(TEnum value)
    {
        Value = value;
    }

    internal PreservedEnum(TEnum value, string rawToken)
    {
        Value = value;
        RawToken = rawToken;
    }

    internal static PreservedEnum<TEnum> FromUnknownToken(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new PreservedEnum<TEnum>(UnknownValue, document.RootElement.GetRawText());
    }

    public static implicit operator PreservedEnum<TEnum>(TEnum value) => new(value);
}
