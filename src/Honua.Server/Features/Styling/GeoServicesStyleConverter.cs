// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Server.Features.Styling;

internal sealed class GeoServicesStyleConverter : IGeoServicesStyleConverter
{
    public GeoServicesStyleConversionResult Convert(
        JsonElement drawingInfo,
        int layerId,
        string layerName,
        MetadataV2GeometryType geometryType)
    {
        var descriptor = new StyleLayerDescriptor(layerId, layerName ?? string.Empty, geometryType);
        var result = GeoServicesToMapLibreConverter.Convert(drawingInfo, descriptor);
        return new GeoServicesStyleConversionResult(result.MapLibreStyleJson, result.Unsupported);
    }
}
