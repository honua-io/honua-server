// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

internal static class MetadataV2EnumNames
{
    internal static string ToJsonName(this MetadataV2FieldType value)
        => value switch
        {
            MetadataV2FieldType.Unknown => "unknown",
            MetadataV2FieldType.String => "string",
            MetadataV2FieldType.Integer => "integer",
            MetadataV2FieldType.BigInteger => "biginteger",
            MetadataV2FieldType.Double => "double",
            MetadataV2FieldType.Float => "float",
            MetadataV2FieldType.Boolean => "boolean",
            MetadataV2FieldType.DateTime => "datetime",
            MetadataV2FieldType.Date => "date",
            MetadataV2FieldType.Time => "time",
            MetadataV2FieldType.Json => "json",
            MetadataV2FieldType.Binary => "binary",
            MetadataV2FieldType.Uuid => "uuid",
            MetadataV2FieldType.Geometry => "geometry",
            MetadataV2FieldType.Geography => "geography",
            _ => value.ToString(),
        };

    internal static string ToJsonName(this MetadataV2GeometryType value)
        => value switch
        {
            MetadataV2GeometryType.None => "none",
            MetadataV2GeometryType.Point => "point",
            MetadataV2GeometryType.MultiPoint => "multipoint",
            MetadataV2GeometryType.LineString => "linestring",
            MetadataV2GeometryType.MultiLineString => "multilinestring",
            MetadataV2GeometryType.Polygon => "polygon",
            MetadataV2GeometryType.MultiPolygon => "multipolygon",
            MetadataV2GeometryType.GeometryCollection => "geometrycollection",
            MetadataV2GeometryType.Mixed => "mixed",
            _ => value.ToString(),
        };
}
