// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Serialization;

internal static class JsonIntegerToken
{
    public static bool IsInteger(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            return false;
        }

        if (!reader.HasValueSequence)
        {
            return !ContainsFractionOrExponent(reader.ValueSpan);
        }

        foreach (var segment in reader.ValueSequence)
        {
            if (ContainsFractionOrExponent(segment.Span))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsFractionOrExponent(ReadOnlySpan<byte> token)
        => token.IndexOfAny((byte)'.', (byte)'e', (byte)'E') >= 0;
}
