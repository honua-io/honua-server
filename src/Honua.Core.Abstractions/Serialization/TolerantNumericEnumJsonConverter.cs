// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Serialization;

internal sealed class TolerantNumericEnumJsonConverter<TEnum> : TolerantEnumJsonConverter<TEnum>
    where TEnum : struct, Enum
{
    internal override PreservedEnum<TEnum> ReadPreserved(ref Utf8JsonReader reader, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? PreservedEnum<TEnum>.FromUnknownToken(ref reader)
            : base.ReadPreserved(ref reader, options);

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteNumberValue(Convert.ToInt32(KnownOrUnknown(value), CultureInfo.InvariantCulture));
}
