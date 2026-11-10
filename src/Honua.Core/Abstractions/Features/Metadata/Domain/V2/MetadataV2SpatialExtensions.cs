// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2SpatialExtensions
{
    public static int? ReadSrid(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Spatial?.SpatialReference?.ResolveSrid();
    }

    public static MetadataV2Bbox? ReadBbox(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Spatial?.Bbox;
    }

    public static MetadataV2GeometryType ReadGeometryType(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
    }

    public static bool HasGeometry(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.ReadGeometryType() != MetadataV2GeometryType.None
            || resource.FindPrimaryGeometryField() is not null;
    }

    public static MetadataV2Field? FindPrimaryGeometryField(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var explicitName = resource.Spatial?.PrimaryGeometryField;
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            foreach (var field in resource.SchemaFields.Where(field => string.Equals(field.Name, explicitName, StringComparison.OrdinalIgnoreCase)))
            {
                return field;
            }
        }

        foreach (var field in resource.SchemaFields)
        {
            for (var i = 0; i < field.SemanticRoles.Count; i++)
            {
                if (string.Equals(field.SemanticRoles[i], "geometry.primary", StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
        }

        foreach (var field in resource.SchemaFields.Where(field => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
        {
            return field;
        }

        return null;
    }

    public static MetadataV2TemporalFields ReadTemporalFields(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var t = resource.Temporal;
        if (t is null)
        {
            return new MetadataV2TemporalFields(null, null, null);
        }
        return new MetadataV2TemporalFields(t.StartTimeField, t.EndTimeField, t.TrackIdField);
    }

    public static MetadataV2Field? FindPrimaryIdField(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (var field in resource.SchemaFields)
        {
            for (var i = 0; i < field.SemanticRoles.Count; i++)
            {
                if (string.Equals(field.SemanticRoles[i], "id.primary", StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
        }
        foreach (var field in resource.SchemaFields.Where(field =>
            string.Equals(field.Name, "objectid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase)))
        {
            return field;
        }
        return null;
    }
}

public readonly record struct MetadataV2TemporalFields(string? StartTimeField, string? EndTimeField, string? TrackIdField);
