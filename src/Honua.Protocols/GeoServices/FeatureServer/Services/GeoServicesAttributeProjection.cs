// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// GeoServices has no array or object field type, so those values are emitted as JSON text; leaving them as arrays makes arcpy return zero rows, while OGC API Features must keep real arrays.
internal static class GeoServicesAttributeProjection
{
    // Arrays and objects are emitted as their raw JSON text so the value stays round-trippable and matches the WMS GetFeatureInfo text for the same cell.
    public static object? ToEsriValue(object? value)
        => FeatureAttributeValueNormalizer.Normalize(value) switch
        {
            JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object } element
                => element.GetRawText(),
            var normalized => normalized,
        };
}
