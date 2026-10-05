// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.ImageServer.Raster;

/// <summary>Maps stored raster sample types onto the GeoServices <c>pixelType</c> vocabulary.</summary>
internal static class ImageServerPixelTypes
{
    /// <summary>The <c>pixelType</c> token that asks for the service's own sample type.</summary>
    internal const string Unknown = "UNKNOWN";

    /// <summary>Returns the GeoServices pixel type for a PostGIS sample type, or null when unmapped.</summary>
    internal static string? ToEsriPixelType(string? storedPixelType)
        => storedPixelType?.ToUpperInvariant() switch
        {
            "1BB" => "U1",
            "2BUI" => "U2",
            "4BUI" => "U4",
            "8BUI" => "U8",
            "8BSI" => "S8",
            "16BUI" => "U16",
            "16BSI" => "S16",
            "32BUI" => "U32",
            "32BSI" => "S32",
            "32BF" => "F32",
            "64BF" => "F64",
            _ => null,
        };

    /// <summary>
    /// True when a requested <c>pixelType</c> needs no conversion of the stored samples: it is
    /// omitted, <c>UNKNOWN</c>, or names the stored type itself.
    /// </summary>
    internal static bool IsNative(string? requestedPixelType, string? storedPixelType)
        => string.IsNullOrWhiteSpace(requestedPixelType)
           || string.Equals(requestedPixelType.Trim(), Unknown, StringComparison.OrdinalIgnoreCase)
           || string.Equals(requestedPixelType.Trim(), ToEsriPixelType(storedPixelType), StringComparison.OrdinalIgnoreCase);
}
