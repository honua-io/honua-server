// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.ImageServer.Raster;

internal static class ImageServerPixelTypes
{
    internal const string Unknown = "UNKNOWN";

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

    internal static bool IsNative(string? requestedPixelType, string? storedPixelType)
        => string.IsNullOrWhiteSpace(requestedPixelType)
           || string.Equals(requestedPixelType.Trim(), Unknown, StringComparison.OrdinalIgnoreCase)
           || string.Equals(requestedPixelType.Trim(), ToEsriPixelType(storedPixelType), StringComparison.OrdinalIgnoreCase);
}
