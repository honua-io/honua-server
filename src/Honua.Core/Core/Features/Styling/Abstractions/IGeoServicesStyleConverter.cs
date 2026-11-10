// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

// The converter lives in Core so an import pipeline can attach a style without a dependency on the server project.
public interface IGeoServicesStyleConverter
{
    // Unsupported symbolizers must be surfaced as import warnings rather than dropped silently.
    GeoServicesStyleConversionResult Convert(
        JsonElement drawingInfo,
        int layerId,
        string layerName,
        MetadataV2GeometryType geometryType);
}

public sealed record GeoServicesStyleConversionResult(
    string MapLibreStyleJson,
    IReadOnlyList<UnsupportedSymbolizerInfo> Unsupported);
