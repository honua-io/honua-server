// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

[JsonConverter(typeof(MetadataV2FolderSelectionConverter))]
public readonly record struct MetadataV2FolderSelection
{
    public MetadataV2FolderSelection(string? value)
    {
        Value = value;
        IsSpecified = true;
    }

    public string? Value { get; }
    public bool IsSpecified { get; }
}

public sealed class MetadataV2FolderSelectionConverter : JsonConverter<MetadataV2FolderSelection>
{
    public override bool HandleNull => true;

    public override MetadataV2FolderSelection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
            throw new JsonException("Folder selection must be a folder id or null for root.");
        return new(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, MetadataV2FolderSelection value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
