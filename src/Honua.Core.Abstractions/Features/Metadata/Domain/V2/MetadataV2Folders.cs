// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2Folder
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("previousNames")]
    public IReadOnlyList<string> PreviousNames { get; init; } = [];

    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }
}

public sealed record MetadataV2ServicePathAlias
{
    [JsonPropertyName("tenant")]
    public string? Tenant { get; init; }

    [JsonPropertyName("folderName")]
    public string? FolderName { get; init; }

    [JsonPropertyName("publicName")]
    public string PublicName { get; init; } = string.Empty;

    [JsonPropertyName("targetFacetIds")]
    public IReadOnlyList<string> TargetFacetIds { get; init; } = [];

    [JsonPropertyName("retired")]
    public bool Retired { get; init; }
}
