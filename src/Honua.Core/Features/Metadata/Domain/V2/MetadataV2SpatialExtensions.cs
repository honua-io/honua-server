// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Helpers for reading the loose-typed spatial/temporal extent metadata carried on
/// <see cref="MetadataV2Resource.Spatial"/> and <see cref="MetadataV2Resource.Temporal"/>.
/// These are <see cref="JsonElement"/>-typed by design so consumers can carry arbitrary
/// projection-specific fields; this helper exposes the common ones that every
/// protocol handler asks for (SRID, bbox, geometry type, time fields).
///
/// Convention for the <c>spatial</c> object:
/// <code>
/// {
///   "srid": 4326,
///   "crs": "EPSG:4326",
///   "geometryType": "Point|LineString|Polygon|...",
///   "bbox": { "west": -180, "south": -90, "east": 180, "north": 90 }
/// }
/// </code>
///
/// Convention for the <c>temporal</c> object:
/// <code>
/// {
///   "startTimeField": "start_time",
///   "endTimeField": "end_time",
///   "trackIdField": "track_id"
/// }
/// </code>
/// </summary>
public static class MetadataV2SpatialExtensions
{
    /// <summary>
    /// Reads the SRID from a resource's spatial extension. Looks at both
    /// <c>spatial.srid</c> (int) and <c>spatial.crs</c> (string like "EPSG:4326").
    /// </summary>
    public static int? ReadSrid(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Spatial is not { ValueKind: JsonValueKind.Object } spatial)
        {
            return null;
        }

        if (spatial.TryGetProperty("srid", out var sridProperty) &&
            sridProperty.ValueKind == JsonValueKind.Number &&
            sridProperty.TryGetInt32(out var sridValue))
        {
            return sridValue;
        }

        if (spatial.TryGetProperty("crs", out var crsProperty) &&
            crsProperty.ValueKind == JsonValueKind.String)
        {
            var crs = crsProperty.GetString();
            if (TryParseEpsg(crs, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the bounding box <c>{west, south, east, north}</c> from the spatial extension.
    /// </summary>
    public static MetadataV2Bbox? ReadBbox(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Spatial is not { ValueKind: JsonValueKind.Object } spatial ||
            !spatial.TryGetProperty("bbox", out var bbox) ||
            bbox.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryReadDouble(bbox, "west", out var west) &&
            TryReadDouble(bbox, "south", out var south) &&
            TryReadDouble(bbox, "east", out var east) &&
            TryReadDouble(bbox, "north", out var north))
        {
            return new MetadataV2Bbox(west, south, east, north);
        }

        return null;
    }

    /// <summary>
    /// Reads the canonical geometry type from the spatial extension (e.g. "Point", "Polygon").
    /// </summary>
    public static string? ReadGeometryType(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Spatial is not { ValueKind: JsonValueKind.Object } spatial ||
            !spatial.TryGetProperty("geometryType", out var geometryType) ||
            geometryType.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return geometryType.GetString();
    }

    /// <summary>
    /// Reads the temporal field names from the resource's temporal extension.
    /// </summary>
    public static MetadataV2TemporalFields ReadTemporalFields(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Temporal is not { ValueKind: JsonValueKind.Object } temporal)
        {
            return new MetadataV2TemporalFields(null, null, null);
        }

        var start = TryReadString(temporal, "startTimeField");
        var end = TryReadString(temporal, "endTimeField");
        var track = TryReadString(temporal, "trackIdField");
        return new MetadataV2TemporalFields(start, end, track);
    }

    /// <summary>
    /// Returns the first field whose declared <see cref="MetadataV2Field.Type"/> resembles
    /// a primary geometry column, falling back to the first field with semantic role
    /// "geometry.primary".
    /// </summary>
    public static MetadataV2Field? FindPrimaryGeometryField(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (var field in resource.SchemaFields)
        {
            for (int i = 0; i < field.SemanticRoles.Count; i++)
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
    /// Returns the field declaring the "id.primary" semantic role, falling back to any
    /// field named "objectid" or "id" (case-insensitive).
    /// </summary>
    public static MetadataV2Field? FindPrimaryIdField(this MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (var field in resource.SchemaFields)
        {
            for (int i = 0; i < field.SemanticRoles.Count; i++)
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

    private static bool TryParseEpsg(string? crs, out int srid)
    {
        srid = 0;
        if (string.IsNullOrWhiteSpace(crs))
        {
            return false;
        }
        var trimmed = crs.Trim();
        var slashIndex = trimmed.LastIndexOf(':');
        if (slashIndex < 0)
        {
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }
        var suffix = trimmed[(slashIndex + 1)..];
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
    }

    private static bool TryReadDouble(JsonElement parent, string name, out double value)
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }
        value = 0;
        return false;
    }

    private static string? TryReadString(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }
}

/// <summary>
/// Maps the v1 service-protocol string identifiers (used by <c>ServiceProtocols</c> /
/// <c>LayerValidationHelpers</c> overloads that still accept a protocol name) onto
/// <see cref="MetadataV2ServiceType"/>. Useful for adapter call sites still expressed
/// in terms of v1 protocol strings.
/// </summary>
public static class MetadataV2ServiceTypeMapping
{
    /// <summary>
    /// Returns the V2 service type matching a v1 protocol name (case-insensitive).
    /// Known names include OData, OgcFeatures, FeatureServer, MapServer, ImageServer,
    /// Wms, Wmts, Wcs, Wfs, Stac, Records, Dcat. Returns null when no match is found.
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
/// Bounding box extracted from a <see cref="MetadataV2Resource"/>'s spatial extension.
/// </summary>
public readonly record struct MetadataV2Bbox(double West, double South, double East, double North);

/// <summary>
/// Temporal field names extracted from a <see cref="MetadataV2Resource"/>'s temporal extension.
/// </summary>
public readonly record struct MetadataV2TemporalFields(string? StartTimeField, string? EndTimeField, string? TrackIdField);
