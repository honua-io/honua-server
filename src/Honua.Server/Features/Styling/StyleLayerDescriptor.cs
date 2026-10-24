// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Styling;

internal readonly record struct StyleLayerDescriptor(
    int Id,
    string Name,
    MetadataV2GeometryType GeometryType,
    bool IsBoundToStorageLayer = true,
    string? SourceName = null,
    string? SourceLayer = null)
{
    public static StyleLayerDescriptor FromResource(MetadataV2Resource resource, int storageLayerId)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new StyleLayerDescriptor(
            storageLayerId,
            resource.Metadata.Name,
            resource.ReadGeometryType());
    }
}
