// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// Default implementation for layer style orchestration.
/// </summary>
internal sealed class LayerStyleService : ILayerStyleService
{
    private static readonly IReadOnlyList<UnsupportedSymbolizerInfo> NoUnsupportedSymbolizers =
        Array.Empty<UnsupportedSymbolizerInfo>();

    private readonly ILayerStyleCatalog _styleCatalog;
    private readonly ILogger<LayerStyleService> _logger;

    public LayerStyleService(ILayerStyleCatalog styleCatalog, ILogger<LayerStyleService> logger)
    {
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<LayerStyleSnapshot?> GetStyleAsync(LayerDefinition layer, CancellationToken cancellationToken = default)
    {
        var stored = await _styleCatalog.GetLayerStyleAsync(layer.Id, cancellationToken).ConfigureAwait(false);
        if (stored == null)
        {
            return null;
        }

        var mapLibreJson = stored.MapLibreStyleJson;
        if (string.IsNullOrWhiteSpace(mapLibreJson))
        {
            var defaultStyle = StyleDefaults.BuildDefaultMapLibreStyle(layer);
            mapLibreJson = StyleJsonUtilities.Serialize(defaultStyle);
            var updated = await _styleCatalog
                .SetMapLibreStyleAsync(layer.Id, mapLibreJson, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (updated == null)
            {
                return null;
            }

            stored = updated;
        }

        var drawingInfoJson = stored.DrawingInfoJson;
        if (string.IsNullOrWhiteSpace(drawingInfoJson))
        {
            drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
            _ = await _styleCatalog.SetDrawingInfoAsync(layer.Id, drawingInfoJson, cancellationToken).ConfigureAwait(false);
        }

        return BuildSnapshot(stored, mapLibreJson, drawingInfoJson);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetDrawingInfoAsync(LayerDefinition layer, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetStyleAsync(layer, cancellationToken).ConfigureAwait(false);
        return snapshot?.DrawingInfo;
    }

    /// <inheritdoc />
    public async Task<LayerStyleUpdateResult> UpdateStyleAsync(
        LayerDefinition layer,
        JsonElement? mapLibreStyle,
        JsonElement? drawingInfo,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        var hasMapLibre = mapLibreStyle.HasValue && mapLibreStyle.Value.ValueKind != JsonValueKind.Null;
        var hasDrawingInfo = drawingInfo.HasValue && drawingInfo.Value.ValueKind != JsonValueKind.Null;

        if (!hasMapLibre && !hasDrawingInfo)
        {
            return new LayerStyleUpdateResult(
                LayerStyleUpdateStatus.Invalid,
                null,
                "Either mapLibreStyle or drawingInfo must be provided.");
        }

        if (hasMapLibre)
        {
            if (!MapLibreStyleNormalizer.TryNormalize(mapLibreStyle!.Value, layer, out var normalized, out var error))
            {
                return new LayerStyleUpdateResult(LayerStyleUpdateStatus.Invalid, null, error);
            }

            var updated = await _styleCatalog
                .SetMapLibreStyleAsync(layer.Id, normalized, revisedBy, changeSummary, cancellationToken)
                .ConfigureAwait(false);
            if (updated == null)
            {
                return new LayerStyleUpdateResult(LayerStyleUpdateStatus.NotFound, null, null);
            }

            var generatedDrawingInfoJson = MapLibreToGeoServicesConverter.Convert(normalized, layer);

            return new LayerStyleUpdateResult(
                LayerStyleUpdateStatus.Updated,
                BuildSnapshot(updated, normalized, generatedDrawingInfoJson),
                null,
                NoUnsupportedSymbolizers);
        }

        var drawingInfoJson = drawingInfo!.Value.GetRawText();
        if (TryGetRendererType(drawingInfo.Value, out var rendererType)
            && !IsSupportedRendererType(rendererType))
        {
            LayerStyleLog.UnsupportedRendererType(_logger, rendererType ?? "unknown", layer.Id);
        }

        var conversion = GeoServicesToMapLibreConverter.Convert(drawingInfo.Value, layer);
        var mapLibreJson = conversion.MapLibreStyleJson;

        var saved = await _styleCatalog
            .SetStyleAsync(layer.Id, mapLibreJson, drawingInfoJson, revisedBy, changeSummary, cancellationToken)
            .ConfigureAwait(false);
        if (saved == null)
        {
            return new LayerStyleUpdateResult(LayerStyleUpdateStatus.NotFound, null, null);
        }

        return new LayerStyleUpdateResult(
            LayerStyleUpdateStatus.Updated,
            BuildSnapshot(saved, mapLibreJson, drawingInfoJson),
            null,
            conversion.Unsupported);
    }

    private static LayerStyleSnapshot BuildSnapshot(LayerStyleDefinition stored, string mapLibreJson, string drawingInfoJson)
    {
        return new LayerStyleSnapshot(
            StyleJsonUtilities.ParseJsonElement(mapLibreJson),
            StyleJsonUtilities.ParseJsonElement(drawingInfoJson),
            stored.StyleVersion,
            stored.StyleRevisedAt,
            stored.StyleRevisedBy,
            stored.StyleChangeSummary);
    }

    private static bool TryGetRendererType(JsonElement drawingInfo, out string? rendererType)
    {
        rendererType = null;

        if (!drawingInfo.TryGetProperty("renderer", out var renderer) || renderer.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!renderer.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        rendererType = typeElement.GetString();
        return !string.IsNullOrWhiteSpace(rendererType);
    }

    private static bool IsSupportedRendererType(string? rendererType)
        => string.Equals(rendererType, "simple", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rendererType, "uniqueValue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rendererType, "classBreaks", StringComparison.OrdinalIgnoreCase);
}
