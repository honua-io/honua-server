// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoservicesImportSelection
{
    public required string ServiceUrl { get; init; }

    public string? ServiceName { get; init; }

    public string? PublicName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MetadataV2FolderSelection FolderId { get; init; }

    public string? Namespace { get; init; }

    public GeoservicesImportPlan? Plan { get; init; }
}
