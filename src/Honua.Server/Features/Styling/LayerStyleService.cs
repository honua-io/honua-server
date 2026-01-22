// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Default implementation for layer style orchestration.
/// </summary>
internal sealed class LayerStyleService : ILayerStyleService
{
    private readonly ILayerStyleCatalog _styleCatalog;

    public LayerStyleService(ILayerStyleCatalog styleCatalog)
    {
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
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
            var updated = await _styleCatalog.SetMapLibreStyleAsync(layer.Id, mapLibreJson, cancellationToken)
                .ConfigureAwait(false);
            if (updated == null)
            {
                return null;
            }
        }

        var drawingInfoJson = stored.DrawingInfoJson;
        if (string.IsNullOrWhiteSpace(drawingInfoJson))
        {
            drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson, layer);
            _ = await _styleCatalog.SetDrawingInfoAsync(layer.Id, drawingInfoJson, cancellationToken).ConfigureAwait(false);
        }

        return new LayerStyleSnapshot(
            StyleJsonUtilities.ParseJsonElement(mapLibreJson),
            StyleJsonUtilities.ParseJsonElement(drawingInfoJson));
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

            var updated = await _styleCatalog.SetMapLibreStyleAsync(layer.Id, normalized, cancellationToken)
                .ConfigureAwait(false);
            if (updated == null)
            {
                return new LayerStyleUpdateResult(LayerStyleUpdateStatus.NotFound, null, null);
            }

            var generatedDrawingInfoJson = MapLibreToGeoServicesConverter.Convert(normalized, layer);
            _ = await _styleCatalog.SetDrawingInfoAsync(layer.Id, generatedDrawingInfoJson, cancellationToken).ConfigureAwait(false);

            return new LayerStyleUpdateResult(
                LayerStyleUpdateStatus.Updated,
                new LayerStyleSnapshot(
                    StyleJsonUtilities.ParseJsonElement(normalized),
                    StyleJsonUtilities.ParseJsonElement(generatedDrawingInfoJson)),
                null);
        }

        var drawingInfoJson = drawingInfo!.Value.GetRawText();
        var mapLibreJson = GeoServicesToMapLibreConverter.Convert(drawingInfo.Value, layer);

        var saved = await _styleCatalog.SetStyleAsync(layer.Id, mapLibreJson, drawingInfoJson, cancellationToken)
            .ConfigureAwait(false);
        if (saved == null)
        {
            return new LayerStyleUpdateResult(LayerStyleUpdateStatus.NotFound, null, null);
        }

        return new LayerStyleUpdateResult(
            LayerStyleUpdateStatus.Updated,
            new LayerStyleSnapshot(
                StyleJsonUtilities.ParseJsonElement(mapLibreJson),
                StyleJsonUtilities.ParseJsonElement(drawingInfoJson)),
            null);
    }
}
