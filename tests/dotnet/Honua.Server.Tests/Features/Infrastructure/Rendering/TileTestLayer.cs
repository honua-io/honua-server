// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

internal static class TileTestLayer
{
    internal static MetadataV2Object Resource { get; } = Create(hiddenField: null);

    internal static MetadataV2Object Create(string? hiddenField)
    {
        var fields = ImmutableArray.Create(
            new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String });
        if (hiddenField is not null)
        {
            fields = fields.Add(new MetadataV2Field { Name = hiddenField, Type = MetadataV2FieldType.String, Hidden = true });
        }

        return new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-test", Name = "test" },
            Spatial = new MetadataV2ObjectSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            SchemaFields = [.. fields]
        };
    }
}
