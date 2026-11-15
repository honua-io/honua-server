// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

// Keep the legacy type token readable by rc.2 while the canonical property uses object vocabulary.
internal sealed class MetadataV2ObjectTypeAliasJsonConverter : PreservedEnumJsonConverter<MetadataV2ObjectType>
{
    public override PreservedEnumValue<MetadataV2ObjectType> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("external-object"))
        {
            return MetadataV2ObjectType.ExternalResource;
        }

        return base.Read(ref reader, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, PreservedEnumValue<MetadataV2ObjectType> value,
        JsonSerializerOptions options)
    {
        if (value.Value == MetadataV2ObjectType.ExternalResource && value.RawToken is null)
        {
            writer.WriteStringValue("external-object");
            return;
        }

        base.Write(writer, value, options);
    }
}
