// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2SpatialReference : ExtensibleJsonRecord
{
    [JsonPropertyName("srid")]
    public int? Srid { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    [JsonPropertyName("isGeographic")]
    public bool IsGeographic { get; init; }

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

        // Handle OGC CRS84 / WGS84 URIs that have no numeric SRID.
        if (trimmed.Equals("http://www.opengis.net/def/crs/OGC/1.3/CRS84", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return 4326;
        }

        // Handles all three documented CRS string forms:
        //   • "EPSG:4326"                                     — colon separator
        //   • "urn:ogc:def:crs:EPSG::4326"                   — colon separator, last token is the code
        //   • "http://www.opengis.net/def/crs/EPSG/0/4326"   — slash-separated URI (no trailing colon)
        var separatorIndex = trimmed.LastIndexOfAny([':', '/']);
        var suffix = separatorIndex < 0 ? trimmed : trimmed[(separatorIndex + 1)..];
        if (string.IsNullOrEmpty(suffix) && separatorIndex > 0)
        {
            // Trailing separator edge-case: skip back one more token.
            var inner = trimmed[..separatorIndex];
            var innerSep = inner.LastIndexOfAny([':', '/']);
            suffix = innerSep < 0 ? inner : inner[(innerSep + 1)..];
        }

        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static MetadataV2SpatialReference Wgs84 { get; } = new()
    {
        Srid = 4326,
        Crs = "EPSG:4326",
        IsGeographic = true
    };

    public static MetadataV2SpatialReference WebMercator { get; } = new()
    {
        Srid = 3857,
        Crs = "EPSG:3857",
        IsGeographic = false
    };
}

public sealed record MetadataV2Bbox : ExtensibleJsonRecord
{
    [JsonPropertyName("west")]
    public double West { get; init; }

    [JsonPropertyName("south")]
    public double South { get; init; }

    [JsonPropertyName("east")]
    public double East { get; init; }

    [JsonPropertyName("north")]
    public double North { get; init; }
}

public sealed record MetadataV2ResourceSpatial : ExtensibleJsonRecord
{
    private IReadOnlyList<MetadataV2SpatialReference> _supportedCrs = Array.Empty<MetadataV2SpatialReference>();

    [JsonPropertyName("spatialReference")]
    public MetadataV2SpatialReference? SpatialReference { get; init; }

    [JsonIgnore]
    public MetadataV2GeometryType GeometryType
    {
        get => GeometryTypeValue.Value;
        init => GeometryTypeValue = value;
    }

    [JsonPropertyName("geometryType")]
    [JsonConverter(typeof(PreservedEnumJsonConverter<MetadataV2GeometryType>))]
    public PreservedEnumValue<MetadataV2GeometryType> GeometryTypeValue { get; init; } = MetadataV2GeometryType.None;

    [JsonPropertyName("bbox")]
    public MetadataV2Bbox? Bbox { get; init; }

    [JsonPropertyName("primaryGeometryField")]
    public string? PrimaryGeometryField { get; init; }

    [JsonPropertyName("supportedCrs")]
    public IReadOnlyList<MetadataV2SpatialReference> SupportedCrs
    {
        get => _supportedCrs;
        init => _supportedCrs = value ?? Array.Empty<MetadataV2SpatialReference>();
    }

    [JsonPropertyName("storageCrs")]
    public MetadataV2SpatialReference? StorageCrs { get; init; }

    [JsonPropertyName("storageCrsCoordinateEpoch")]
    public double? StorageCrsCoordinateEpoch { get; init; }
}
