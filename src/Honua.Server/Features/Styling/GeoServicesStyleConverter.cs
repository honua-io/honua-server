// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Adapter exposing the in-server <see cref="GeoServicesToMapLibreConverter"/>
/// via the public <see cref="IGeoServicesStyleConverter"/> abstraction so that
/// provider import pipelines (Postgres, etc.) can translate Esri renderers to
/// canonical MapLibre style JSON at publish time without depending on
/// <c>Honua.Server</c> internals.
/// </summary>
internal sealed class GeoServicesStyleConverter : IGeoServicesStyleConverter
{
    /// <inheritdoc />
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
