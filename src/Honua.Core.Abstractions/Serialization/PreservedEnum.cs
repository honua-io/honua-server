// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

public readonly record struct PreservedEnum<TEnum> where TEnum : struct, Enum
{
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

    internal static PreservedEnum<TEnum> FromToken(TEnum value, ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null ||
            !EqualityComparer<TEnum>.Default.Equals(value, Enum.Parse<TEnum>("Unknown")))
        {
            return new PreservedEnum<TEnum>(value);
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return new PreservedEnum<TEnum>(value, document.RootElement.GetRawText());
    }

    public static implicit operator PreservedEnum<TEnum>(TEnum value) => new(value);
}
