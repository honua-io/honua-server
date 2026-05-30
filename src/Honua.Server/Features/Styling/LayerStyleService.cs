// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Default implementation for layer style orchestration.
/// </summary>
internal sealed class LayerStyleService : ILayerStyleService
{
    private static readonly IReadOnlyList<UnsupportedSymbolizerInfo> NoUnsupportedSymbolizers =
        Array.Empty<UnsupportedSymbolizerInfo>();

    private readonly ILayerStyleCatalog _styleCatalog;
    private readonly ILogger<LayerStyleService> _logger;
    private readonly IMetadataV2GraphProvider? _metadataV2GraphProvider;

    public LayerStyleService(
        ILayerStyleCatalog styleCatalog,
        ILogger<LayerStyleService> logger,
        IMetadataV2GraphProvider? metadataV2GraphProvider = null)
    {
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metadataV2GraphProvider = metadataV2GraphProvider;
    }

    /// <inheritdoc />
    public async Task<LayerStyleSnapshot?> GetStyleAsync(
        MetadataV2Resource resource,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var styleLayer = await ResolveStyleLayerAsync(resource, layerId, cancellationToken).ConfigureAwait(false);
        var stored = await _styleCatalog.GetLayerStyleAsync(styleLayer.Id, cancellationToken).ConfigureAwait(false);
        if (stored == null)
        {
            return null;
        }

        var mapLibreJson = stored.MapLibreStyleJson;
        var hasStoredMapLibre = !string.IsNullOrWhiteSpace(mapLibreJson);
        if (!hasStoredMapLibre)
        {
            // Lazy default for newly-published layers that have not yet
            // received a PUT.  Computed in memory only — persisting it would
            // increment style_version and stamp style_revised_at on a read
            // path, contradicting the PUT-only revision contract documented
            // in docs/gis/style-engine-protocol-consumption.md.  The first
            // PUT lands the genuine revision metadata.
            var defaultStyle = StyleDefaults.BuildDefaultMapLibreStyle(styleLayer);
            mapLibreJson = StyleJsonUtilities.Serialize(defaultStyle);
        }

        var drawingInfoJson = stored.DrawingInfoJson;
        if (string.IsNullOrWhiteSpace(drawingInfoJson))
        {
            drawingInfoJson = MapLibreToGeoServicesConverter.Convert(mapLibreJson!, styleLayer);
            if (hasStoredMapLibre)
            {
                // Cache the back-generated drawingInfo only when we have a
                // canonical MapLibre row to mirror.  SetDrawingInfoAsync does
                // not touch revision metadata, so this preserves the PUT-only
                // contract.  For unstyled layers the drawingInfo column stays
                // NULL until the first PUT lands a canonical row.
                _ = await _styleCatalog.SetDrawingInfoAsync(styleLayer.Id, drawingInfoJson, cancellationToken).ConfigureAwait(false);
            }
        }

        return BuildSnapshot(stored, mapLibreJson!, drawingInfoJson);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetDrawingInfoAsync(
        MetadataV2Resource resource,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetStyleAsync(resource, layerId, cancellationToken).ConfigureAwait(false);
        return snapshot?.DrawingInfo;
    }

    /// <inheritdoc />
    public async Task<LayerStyleUpdateResult> UpdateStyleAsync(
        MetadataV2Resource resource,
        int layerId,
        JsonElement? mapLibreStyle,
        JsonElement? drawingInfo,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        var styleLayer = await ResolveStyleLayerAsync(resource, layerId, cancellationToken).ConfigureAwait(false);
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
            if (!MapLibreStyleNormalizer.TryNormalize(mapLibreStyle!.Value, styleLayer, out var normalized, out var error))
            {
                return new LayerStyleUpdateResult(LayerStyleUpdateStatus.Invalid, null, error);
            }

            var updated = await _styleCatalog
                .SetMapLibreStyleAsync(styleLayer.Id, normalized, revisedBy, changeSummary, cancellationToken)
                .ConfigureAwait(false);
            if (updated == null)
            {
                return new LayerStyleUpdateResult(LayerStyleUpdateStatus.NotFound, null, null);
            }

            var generatedDrawingInfoJson = MapLibreToGeoServicesConverter.Convert(normalized, styleLayer);

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
            LayerStyleLog.UnsupportedRendererType(_logger, rendererType ?? "unknown", styleLayer.Id);
        }

        var conversion = GeoServicesToMapLibreConverter.Convert(drawingInfo.Value, styleLayer);
        var mapLibreJson = conversion.MapLibreStyleJson;

        var saved = await _styleCatalog
            .SetStyleAsync(styleLayer.Id, mapLibreJson, drawingInfoJson, revisedBy, changeSummary, cancellationToken)
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

    private async ValueTask<StyleLayerDescriptor> ResolveStyleLayerAsync(
        MetadataV2Resource resource,
        int layerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var storageLayerId = layerId;
        if (_metadataV2GraphProvider is not null)
        {
            var snapshot = await _metadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Index.StorageBindingsByStorageLayerId.TryGetValue(layerId, out var binding) &&
                string.Equals(binding.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
            {
                storageLayerId = layerId;
            }
            else
            {
                storageLayerId = snapshot.ResolveStorageLayerId(resource) ?? layerId;
            }
        }

        return StyleLayerDescriptor.FromResource(resource, storageLayerId);
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
