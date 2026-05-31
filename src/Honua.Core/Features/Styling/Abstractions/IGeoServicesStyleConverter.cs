// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Converts a GeoServices <c>drawingInfo</c> payload to a canonical MapLibre Style
/// Spec v8 JSON document and reports any symbolizer inputs that could not be
/// losslessly translated.  Exposed in <c>Honua.Core</c> so provider import
/// pipelines (for example, <c>Honua.Postgres</c>) can attach style at publish
/// time without taking a hard dependency on <c>Honua.Server</c>.
/// </summary>
public interface IGeoServicesStyleConverter
{
    /// <summary>
    /// Converts a GeoServices <paramref name="drawingInfo"/> payload to a
    /// canonical MapLibre Style Spec v8 JSON document.  Callers that
    /// receive any <see cref="GeoServicesStyleConversionResult.Unsupported"/>
    /// entries must surface them via the import warnings channel so unsupported
    /// renderers are flagged rather than silently dropped.
    /// </summary>
    /// <param name="drawingInfo">Raw GeoServices <c>drawingInfo</c> payload.</param>
    /// <param name="layerId">Storage layer identifier used to namespace generated MapLibre layer ids.</param>
    /// <param name="layerName">Layer display name used by the canonical style document.</param>
    /// <param name="geometryType">Canonical geometry type for the target layer.</param>
    /// <returns>Conversion result containing the MapLibre JSON and any unsupported-symbolizer reports.</returns>
    GeoServicesStyleConversionResult Convert(
        JsonElement drawingInfo,
        int layerId,
        string layerName,
        MetadataV2GeometryType geometryType);
}

/// <summary>
/// Result of a GeoServices-to-MapLibre style conversion exposed through
/// <see cref="IGeoServicesStyleConverter"/>.  Carries the canonical MapLibre
/// JSON and any symbolizers that could not be losslessly translated so callers
/// can persist a fidelity report alongside the style.
/// </summary>
public sealed record GeoServicesStyleConversionResult(
    string MapLibreStyleJson,
    IReadOnlyList<UnsupportedSymbolizerInfo> Unsupported);
