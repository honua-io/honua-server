// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

internal static class MetadataV2EnumNames
{
    public static string ToWireName(this MetadataV2FieldType type)
        => type switch
        {
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
            _ => "unknown",
        };
}
