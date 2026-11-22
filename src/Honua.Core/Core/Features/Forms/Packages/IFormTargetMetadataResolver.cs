// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Forms.Packages;

public interface IFormTargetMetadataResolver
{
    Task<FormTargetMetadataResolution> ResolveAsync(
        FormTargetDefinition? target,
        CancellationToken cancellationToken = default);
}

public readonly record struct FormTargetMetadataResolution(
    MetadataV2Service? Service,
    MetadataV2Layer? Publication,
    MetadataV2Object? Resource,
    int? StorageLayerId)
{
    // The target publication is served from a source table, so the managed writer cannot edit it.
    public bool ServedFromSourceTable { get; init; }
}
