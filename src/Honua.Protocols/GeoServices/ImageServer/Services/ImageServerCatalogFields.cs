// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerCatalogFields
{
    public static readonly IReadOnlyList<string> CanonicalNames =
    [
        "OBJECTID",
        "Name",
        "MinPS",
        "MaxPS",
        "LowPS",
        "HighPS",
        "CenterX",
        "CenterY",
        "ZOrder",
        "Shape_Length",
        "Shape_Area",
        "BandCount",
        "PixelType",
        "AcquisitionDate",
        "CreatedAt",
    ];

    public static Field[] Schema(ImmutableArray<string> maskedFields = default)
    {
        Field[] allFields =
        [
            new Field { Name = "OBJECTID", Type = "esriFieldTypeOID", Alias = "OBJECTID" },
            new Field { Name = "Name", Type = "esriFieldTypeString", Alias = "Name", Nullable = true },
            new Field { Name = "MinPS", Type = "esriFieldTypeDouble", Alias = "MinPS" },
            new Field { Name = "MaxPS", Type = "esriFieldTypeDouble", Alias = "MaxPS" },
            new Field { Name = "LowPS", Type = "esriFieldTypeDouble", Alias = "LowPS" },
            new Field { Name = "HighPS", Type = "esriFieldTypeDouble", Alias = "HighPS" },
            new Field { Name = "CenterX", Type = "esriFieldTypeDouble", Alias = "CenterX" },
            new Field { Name = "CenterY", Type = "esriFieldTypeDouble", Alias = "CenterY" },
            new Field { Name = "ZOrder", Type = "esriFieldTypeInteger", Alias = "ZOrder" },
            new Field { Name = "Shape_Length", Type = "esriFieldTypeDouble", Alias = "Shape_Length" },
            new Field { Name = "Shape_Area", Type = "esriFieldTypeDouble", Alias = "Shape_Area" },
            new Field { Name = "BandCount", Type = "esriFieldTypeInteger", Alias = "BandCount" },
            new Field { Name = "PixelType", Type = "esriFieldTypeString", Alias = "PixelType", Nullable = true },
            new Field { Name = "AcquisitionDate", Type = "esriFieldTypeDate", Alias = "AcquisitionDate", Nullable = true },
            new Field { Name = "CreatedAt", Type = "esriFieldTypeDate", Alias = "CreatedAt" },
        ];

        return maskedFields.IsDefaultOrEmpty
            ? allFields
            : allFields.Where(field => !maskedFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    public static bool TryResolve(string propertyName, ImageServerCatalogItem item, out object? value)
    {
        ArgumentNullException.ThrowIfNull(item);

        switch (Normalize(propertyName))
        {
            case "OBJECTID":
                value = item.ObjectId;
                return true;
            case "NAME":
                value = item.Name;
                return true;
            case "MINPS":
                value = item.MinPixelSize;
                return true;
            case "MAXPS":
                value = item.MaxPixelSize;
                return true;
            case "LOWPS":
                value = item.LowPixelSize;
                return true;
            case "HIGHPS":
                value = item.HighPixelSize;
                return true;
            case "CENTERX":
                value = item.CenterX;
                return true;
            case "CENTERY":
                value = item.CenterY;
                return true;
            case "ZORDER":
                value = item.ZOrder;
                return true;
            case "SHAPE_LENGTH":
                value = item.ShapeLength;
                return true;
            case "SHAPE_AREA":
                value = item.ShapeArea;
                return true;
            case "BANDCOUNT":
            case "NUM_BANDS":
                value = item.BandCount;
                return true;
            case "PIXELTYPE":
            case "PIXEL_TYPE":
                value = item.PixelType;
                return true;
            case "ACQUISITIONDATE":
                value = item.AcquisitionDate?.UtcDateTime;
                return true;
            case "CREATEDAT":
            case "CREATED_AT":
                value = item.CreatedAt.UtcDateTime;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public static string? ResolveCanonicalName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var normalized = Normalize(fieldName);
        var canonicalMatch = CanonicalNames.FirstOrDefault(
            canonical => string.Equals(Normalize(canonical), normalized, StringComparison.Ordinal));
        if (canonicalMatch != null)
        {
            return canonicalMatch;
        }

        return normalized switch
        {
            "NUM_BANDS" => "BandCount",
            "PIXEL_TYPE" => "PixelType",
            "CREATED_AT" => "CreatedAt",
            _ => null,
        };
    }

    private static string Normalize(string fieldName)
        => fieldName.Trim().ToUpperInvariant();
}
