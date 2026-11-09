// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2Folder : ExtensibleJsonRecord
{
    private IReadOnlyList<string>? _previousNames;

    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("previousNames")]
    public IReadOnlyList<string> PreviousNames
    {
        get => _previousNames ?? [];
        init => _previousNames = value;
    }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }
}

public sealed record MetadataV2ServicePathAlias : ExtensibleJsonRecord
{
    private IReadOnlyList<string>? _targetFacetIds;

    [JsonPropertyName("tenant")]
    public string? Tenant { get; init; }

    [JsonPropertyName("folderName")]
    public string? FolderName { get; init; }

    [JsonPropertyName("publicName")]
    public string PublicName { get; init; } = string.Empty;

    [JsonPropertyName("targetFacetIds")]
    public IReadOnlyList<string> TargetFacetIds
    {
        get => _targetFacetIds ?? [];
        init => _targetFacetIds = value;
    }

    [JsonPropertyName("retired")]
    public bool Retired { get; init; }
}
