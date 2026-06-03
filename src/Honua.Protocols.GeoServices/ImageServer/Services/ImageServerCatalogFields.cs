// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Central knowledge of the Esri raster-catalog field surface exposed by the
/// Image Server <c>query</c> endpoint. WHERE evaluation, <c>orderByFields</c>
/// ordering, and <c>outFields</c> projection all resolve field names through
/// this helper so the supported set stays in lockstep across the operation.
/// </summary>
internal static class ImageServerCatalogFields
{
    /// <summary>
    /// Canonical, response-facing field names in their natural emit order. These
    /// are the names stamped onto query feature attributes and the field schema.
    /// </summary>
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

    /// <summary>
    /// Resolves a catalog field value for WHERE evaluation, accepting the Esri
    /// field aliases (for example <c>NUM_BANDS</c>) that the SQL surface allows.
    /// Date fields resolve to <see cref="DateTime"/> for comparison.
    /// </summary>
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

    /// <summary>
    /// Maps a caller-supplied field name (any documented alias, case-insensitive)
    /// to its canonical response-facing name, or <c>null</c> when the field is
    /// not part of the catalog surface.
    /// </summary>
    public static string? ResolveCanonicalName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var normalized = Normalize(fieldName);
        foreach (var canonical in CanonicalNames)
        {
            if (string.Equals(Normalize(canonical), normalized, StringComparison.Ordinal))
            {
                return canonical;
            }
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
