// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public static class FieldDefinitionExtensions
{
    public static string ToPostgresType(this string esriType, int? length = null)
    {
        return esriType.ToUpperInvariant() switch
        {
            // OIDs can exceed Int32 on modern ArcGIS (64-bit object ids), and the downstream
            // read path materializes them via GetInt64, so map to a 64-bit column to avoid overflow.
            "ESRIFIELDTYPEOID" or "ESRIFIELDTYPEBIGINTEGER" => "BIGINT",
            "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" => "INTEGER",
            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" => "DOUBLE PRECISION",
            "ESRIFIELDTYPESTRING" => length.HasValue && length > 0 && length <= 8000
                ? $"VARCHAR({length})"
                : "TEXT",
            "ESRIFIELDTYPEDATE" => "TIMESTAMP WITH TIME ZONE",
            "ESRIFIELDTYPEDATEONLY" => "DATE",
            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" => "UUID",
            "ESRIFIELDTYPEBLOB" => "BYTEA",
            "ESRIFIELDTYPERASTER" => "BYTEA",
            "ESRIFIELDTYPEXML" => "XML",
            "ESRIFIELDTYPEGEOMETRY" => "GEOMETRY",
            _ => "TEXT"
        };
    }

    public static string SanitizeFieldName(this string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return "unnamed_field";

        // Replace problematic characters with underscores
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            fieldName, @"[^a-zA-Z0-9_]", "_");

        // Ensure it starts with a letter or underscore
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        // Limit length
        if (sanitized.Length > 63)
            sanitized = sanitized[..63];

        return sanitized.ToLowerInvariant();
    }
}
