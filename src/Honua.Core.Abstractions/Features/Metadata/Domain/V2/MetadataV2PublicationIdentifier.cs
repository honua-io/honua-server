// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

// One identifier replaces the separate index, local id, and path so those three cannot drift.
public sealed record MetadataV2PublicationIdentifier
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("isNumeric")]
    public bool IsNumeric { get; init; }

    [JsonPropertyName("pathOverride")]
    public string? PathOverride { get; init; }
}
