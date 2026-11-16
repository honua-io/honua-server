// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2PairedPublication
{
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }
    [JsonPropertyName("identityField")]
    public required string IdentityField { get; init; }
    [JsonPropertyName("minZoom")]
    public int MinZoom { get; init; }
    [JsonPropertyName("maxZoom")]
    public int MaxZoom { get; init; } = 8;
    [JsonPropertyName("maxTiles")]
    public int MaxTiles { get; init; } = 10000;
}
