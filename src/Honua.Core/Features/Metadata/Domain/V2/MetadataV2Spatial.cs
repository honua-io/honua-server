// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Coordinate reference system carried on Metadata v2 resources and services.
/// At least one of <see cref="Srid"/> or <see cref="Crs"/> is expected; helpers
/// will derive the missing one when possible (e.g. "EPSG:4326" ⇄ 4326).
/// </summary>
public sealed record MetadataV2SpatialReference
{
    /// <summary>
    /// EPSG numeric identifier when known.
    /// </summary>
    [JsonPropertyName("srid")]
    public int? Srid { get; init; }

    /// <summary>
    /// CRS URN or short identifier, e.g. <c>EPSG:4326</c> or
    /// <c>http://www.opengis.net/def/crs/EPSG/0/4326</c>.
    /// </summary>
    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    /// <summary>
    /// True when the CRS uses spherical (geographic) coordinates. WGS84/EPSG:4326
    /// is the canonical example. Used by SQL translators to pick the geographic
    /// vs projected operator (ST_DWithin, ST_Distance, …).
    /// </summary>
    [JsonPropertyName("isGeographic")]
    public bool IsGeographic { get; init; }

    /// <summary>
    /// Returns the SRID, deriving it from <see cref="Crs"/> when <see cref="Srid"/>
    /// is unset and the CRS is an <c>EPSG:</c> identifier.
    /// </summary>
    public int? ResolveSrid()
    {
        if (Srid is int srid)
        {
            return srid;
        }

        if (string.IsNullOrWhiteSpace(Crs))
        {
            return null;
        }

        var trimmed = Crs.Trim();
        var slashIndex = trimmed.LastIndexOf(':');
        var suffix = slashIndex < 0 ? trimmed : trimmed[(slashIndex + 1)..];
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>WGS84 geographic CRS singleton, used as a sensible default.</summary>
    public static MetadataV2SpatialReference Wgs84 { get; } = new()
    {
        Srid = 4326,
        Crs = "EPSG:4326",
        IsGeographic = true
    };

    /// <summary>Web Mercator projected CRS singleton, used by tile services.</summary>
    public static MetadataV2SpatialReference WebMercator { get; } = new()
    {
        Srid = 3857,
        Crs = "EPSG:3857",
        IsGeographic = false
    };
}

/// <summary>
/// Bounding box in the CRS of the owning <see cref="MetadataV2SpatialReference"/>.
/// </summary>
public sealed record MetadataV2Bbox
{
    /// <summary>Minimum x / longitude / easting.</summary>
    [JsonPropertyName("west")]
    public double West { get; init; }

    /// <summary>Minimum y / latitude / northing.</summary>
    [JsonPropertyName("south")]
    public double South { get; init; }

    /// <summary>Maximum x / longitude / easting.</summary>
    [JsonPropertyName("east")]
    public double East { get; init; }

    /// <summary>Maximum y / latitude / northing.</summary>
    [JsonPropertyName("north")]
    public double North { get; init; }
}

/// <summary>
/// Typed spatial metadata for a Metadata v2 resource. Replaces the prior
/// <c>JsonElement?</c> slot — every consumer needed the same four facts (SRID,
/// geometry type, bbox, primary geometry field), so they belong on the typed model.
/// </summary>
public sealed record MetadataV2ResourceSpatial
{
    /// <summary>CRS / spatial reference declared by this resource.</summary>
    [JsonPropertyName("spatialReference")]
    public MetadataV2SpatialReference? SpatialReference { get; init; }

    /// <summary>Geometry kind, or <see cref="MetadataV2GeometryType.None"/> for tables.</summary>
    [JsonPropertyName("geometryType")]
    public MetadataV2GeometryType GeometryType { get; init; } = MetadataV2GeometryType.None;

    /// <summary>Bounding box of the data extent, in the CRS of <see cref="SpatialReference"/>.</summary>
    [JsonPropertyName("bbox")]
    public MetadataV2Bbox? Bbox { get; init; }

    /// <summary>
    /// Name of the field carrying the primary geometry column on this resource.
    /// When unset, callers fall back to the first schema field flagged with the
    /// <c>geometry.primary</c> semantic role and then to the first
    /// <see cref="MetadataV2FieldType.Geometry"/>/<see cref="MetadataV2FieldType.Geography"/>
    /// field.
    /// </summary>
    [JsonPropertyName("primaryGeometryField")]
    public string? PrimaryGeometryField { get; init; }
}
