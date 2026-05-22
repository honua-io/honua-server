// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Helpers for reading the typed <see cref="MetadataV2Resource.Spatial"/> slot.
/// The model used to be a free-form <c>JsonElement?</c>; consumers all needed the
/// same four facts (SRID, geometry type, bbox, primary geometry field), so the slot
/// is now <see cref="MetadataV2ResourceSpatial"/>. These helpers remain as thin
/// extension wrappers so existing call sites can keep using <c>resource.ReadSrid()</c>
/// style — the work happens against the typed properties.
/// </summary>
public static class MetadataV2SpatialExtensions
{
    /// <summary>
    /// Returns the resource's declared SRID, deriving from the CRS string when the
    /// numeric SRID is unset.
    /// </summary>
    public static int? ReadSrid(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Spatial?.SpatialReference?.ResolveSrid();
    }

    /// <summary>
    /// Returns the resource's declared bounding box, in the CRS of the resource's
    /// spatial reference.
    /// </summary>
    public static MetadataV2Bbox? ReadBbox(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Spatial?.Bbox;
    }

    /// <summary>
    /// Returns the resource's declared geometry type as the canonical
    /// <see cref="MetadataV2GeometryType"/> enum value.
    /// </summary>
    public static MetadataV2GeometryType ReadGeometryType(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
    }

    /// <summary>
    /// Returns the field configured as the resource's primary geometry column.
    /// Resolution order: (1) explicit <see cref="MetadataV2ResourceSpatial.PrimaryGeometryField"/>;
    /// (2) the first schema field carrying the <c>geometry.primary</c> semantic role;
    /// (3) the first schema field with a <see cref="MetadataV2FieldType.Geometry"/> or
    /// <see cref="MetadataV2FieldType.Geography"/> type.
    /// </summary>
    public static MetadataV2Field? FindPrimaryGeometryField(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var explicitName = resource.Spatial?.PrimaryGeometryField;
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            foreach (var field in resource.SchemaFields)
            {
                if (string.Equals(field.Name, explicitName, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
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

        foreach (var field in resource.SchemaFields)
        {
            if (field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the temporal field names from the resource's typed temporal slot.
    /// </summary>
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

    /// <summary>
    /// Returns the field declaring the <c>id.primary</c> semantic role, falling back
    /// to any field named <c>objectid</c> or <c>id</c> (case-insensitive).
    /// </summary>
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
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, "objectid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }
        return null;
    }
}

/// <summary>
/// Maps the v1 service-protocol string identifiers onto <see cref="MetadataV2ServiceType"/>.
/// </summary>
public static class MetadataV2ServiceTypeMapping
{
    /// <summary>
    /// Returns the V2 service type matching a v1 protocol name (case-insensitive).
    /// </summary>
    public static MetadataV2ServiceType? Map(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return null;
        }

        return protocol.Trim() switch
        {
            "OData" or "odata" => MetadataV2ServiceType.OData,
            "OgcFeatures" or "ogc-features" or "ogc-api-features" => MetadataV2ServiceType.OgcApiFeatures,
            "FeatureServer" or "feature-server" or "esri-feature-service" => MetadataV2ServiceType.EsriFeatureService,
            "MapServer" or "map-server" or "esri-map-service" => MetadataV2ServiceType.EsriMapService,
            "ImageServer" or "image-server" or "esri-image-service" => MetadataV2ServiceType.EsriImageService,
            "Wms" or "wms" => MetadataV2ServiceType.Wms,
            "Wmts" or "wmts" => MetadataV2ServiceType.Wmts,
            "Wfs" or "wfs" => MetadataV2ServiceType.Wfs,
            "Stac" or "stac" or "stac-api" => MetadataV2ServiceType.StacApi,
            "Dcat" or "dcat" or "dcat-catalog" => MetadataV2ServiceType.DcatCatalog,
            "Records" or "records" or "ogc-records" => MetadataV2ServiceType.OgcRecords,
            _ => null,
        };
    }
}

/// <summary>
/// Temporal field names extracted from a <see cref="MetadataV2Resource"/>'s temporal extension.
/// </summary>
public readonly record struct MetadataV2TemporalFields(string? StartTimeField, string? EndTimeField, string? TrackIdField);
