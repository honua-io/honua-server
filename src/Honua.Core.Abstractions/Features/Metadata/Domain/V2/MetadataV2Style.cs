// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2Style : ExtensibleJsonRecord
{
    private IReadOnlyList<MetadataV2StyleEncoding>? _encodings;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("abstract")]
    public string? Abstract { get; init; }

    [JsonPropertyName("legendUrl")]
    public string? LegendUrl { get; init; }

    [JsonPropertyName("styleVersion")]
    public int StyleVersion { get; init; }

    [JsonPropertyName("encodings")]
    public IReadOnlyList<MetadataV2StyleEncoding> Encodings
    {
        get => _encodings ?? Array.Empty<MetadataV2StyleEncoding>();
        init => _encodings = value;
    }
}

public sealed record MetadataV2StyleEncoding : ExtensibleJsonRecord
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; init; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("storageBindingId")]
    public string? StorageBindingId { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }
}
