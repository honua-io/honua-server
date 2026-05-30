// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Styling;

internal readonly record struct StyleLayerDescriptor(
    int Id,
    string Name,
    MetadataV2GeometryType GeometryType)
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
