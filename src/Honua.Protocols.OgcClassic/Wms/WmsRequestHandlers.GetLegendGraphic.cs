// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wms;

internal static partial class WmsRequestHandlers
{
    private const int DefaultLegendSwatchWidth = 20;
    private const int DefaultLegendSwatchHeight = 20;
    private const int MaxLegendSwatchDimension = 256;
    private const int MaxLegendEntries = 64;

    // WebMercator scale denominator at zoom 0, matching the GoogleMapsCompatible
    // well-known scale set the WMTS port advertises. SCALE is a WMS-level concept;
    // converting it to the canonical zoom the style pipeline understands is adapter work.
    private const double LegendScaleDenominatorZoom0 = 559082264.0287178;

    /// <summary>
    /// Style layer types the raster pipeline actually paints. A style layer of any
    /// other type (symbol, background, heatmap) draws nothing in GetMap, so it must
    /// not contribute a legend entry — a swatch for it would advertise symbology the
    /// map never renders.
    /// </summary>
    private static bool IsLegendRenderableStyleLayer(MapLibreStyleLayer styleLayer)
        => styleLayer.Type is "fill" or "line" or "circle";

    /// <summary>
    /// Whether a layer's resolved style plan can produce a legend that matches GetMap.
    /// An empty plan qualifies: GetMap falls back to the geometry-typed default paints
    /// and the swatch renderer draws from that same default. A plan whose every layer
    /// is unpainted (symbol/background only) does not.
    /// </summary>
    private static bool SupportsLegend(RasterStylePlan stylePlan)
        => stylePlan.StyleLayers.Length == 0 ||
           stylePlan.StyleLayers.Any(IsLegendRenderableStyleLayer);

    private static async Task<IResult> HandleWmsGetLegendGraphic(
        HttpContext context,
        WmsLayer[] accessibleLayers,
        string serviceId,
        ILogger logger)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        OgcClassicLog.WmsRequested(logger, serviceId, "GetLegendGraphic");
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapRender, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcMaps);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wms-getlegendgraphic");

        var query = context.Request.Query;
        if (!TryGetRequiredQueryValue(query, "LAYER", out var layerParam))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "LAYER parameter is required.");
        }

        if (!TryResolveWmsRequestedLayers(accessibleLayers, [layerParam.Trim()], out var resolvedLayers, out var unresolvedLayer) ||
            resolvedLayers.Length != 1)
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedLayer) ? layerParam : unresolvedLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        var styleParam = GetQueryValue(query, "STYLE") ?? string.Empty;
        if (!ValidateWmsStyles(styleParam, 1, out var styleError))
        {
            return CreateWmsServiceException(context, "StyleNotDefined", styleError);
        }

        var formatValue = GetQueryValue(query, "FORMAT");
        if (!string.IsNullOrWhiteSpace(formatValue) &&
            !string.Equals(formatValue.Trim(), "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmsServiceException(context, "InvalidFormat", "FORMAT must be image/png.");
        }

        if (!TryParseLegendDimension(query, "WIDTH", DefaultLegendSwatchWidth, out var swatchWidth, out var widthError))
        {
            return CreateWmsServiceException(context, "InvalidDimensionValue", widthError);
        }

        if (!TryParseLegendDimension(query, "HEIGHT", DefaultLegendSwatchHeight, out var swatchHeight, out var heightError))
        {
            return CreateWmsServiceException(context, "InvalidDimensionValue", heightError);
        }

        if (!TryParseLegendScale(query, out var zoom, out var scaleError))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", scaleError);
        }

        var layer = resolvedLayers[0];
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();

        // Same style source, same cache, same parse as GetMap: the legend cannot show
        // symbology from a style revision the map is not drawing.
        var stylePlan = await GetRasterStylePlanAsync(
            styleCatalog,
            layer.StorageLayerId,
            cancellationToken).ConfigureAwait(false);

        if (!SupportsLegend(stylePlan))
        {
            return CreateWmsServiceException(
                context,
                "OperationNotSupported",
                $"Layer '{GetWmsLayerDisplayName(layer)}' has no legend-renderable symbology.");
        }

        var geometryType = layer.Resource.ReadGeometryType();
        var entries = new List<LegendImageComposer.LegendImageEntry>();
        var unrepresentable = new List<string>();

        if (stylePlan.StyleLayers.Length == 0)
        {
            // No stored style: GetMap paints the geometry-typed default paints, and the
            // swatch renderer draws from those same defaults.
            entries.Add(new LegendImageComposer.LegendImageEntry(
                new MapLibreStyleLayer { Type = "default" },
                new LegendClass(GetWmsLayerDisplayName(layer), ImmutableDictionary<string, object?>.Empty)));
        }

        foreach (var styleLayer in stylePlan.StyleLayers)
        {
            if (!IsLegendRenderableStyleLayer(styleLayer) ||
                !StyleTranslator.ShouldRenderLayer(styleLayer, zoom))
            {
                continue;
            }

            var classSet = LegendClassifier.Classify(styleLayer);
            if (classSet.UnrepresentableReason != null)
            {
                unrepresentable.Add(classSet.UnrepresentableReason);
            }

            foreach (var legendClass in classSet.Classes)
            {
                if (entries.Count == MaxLegendEntries)
                {
                    unrepresentable.Add(
                        $"Legend truncated at {MaxLegendEntries.ToString(CultureInfo.InvariantCulture)} entries.");
                    break;
                }

                entries.Add(new LegendImageComposer.LegendImageEntry(styleLayer, legendClass));
            }

            if (entries.Count == MaxLegendEntries)
            {
                break;
            }
        }

        if (entries.Count == 0)
        {
            // SCALE excluded every style layer. An empty legend is the honest answer:
            // substituting a default swatch would claim symbology this scale never draws.
            unrepresentable.Add("No style layer is visible at the requested SCALE.");
        }

        var imageBytes = LegendImageComposer.Compose(
            entries,
            geometryType,
            swatchWidth,
            swatchHeight,
            SKEncodedImageFormat.Png);

        HonuaTelemetry.SetSuccess(activity, entries.Count);

        return unrepresentable.Count == 0
            ? Results.Bytes(imageBytes, "image/png")
            : new WmsImageResult(imageBytes, "image/png", string.Join(" ", unrepresentable));
    }

    private static bool TryParseLegendDimension(
        IQueryCollection query,
        string parameterName,
        int defaultValue,
        out int value,
        out string error)
    {
        value = defaultValue;
        error = string.Empty;

        var raw = GetQueryValue(query, parameterName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            value <= 0 || value > MaxLegendSwatchDimension)
        {
            error = $"{parameterName} must be an integer between 1 and {MaxLegendSwatchDimension.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Maps the WMS SCALE parameter (a scale denominator) onto the canonical zoom the
    /// style pipeline gates minzoom/maxzoom with. Absent SCALE leaves zoom unknown,
    /// which is what GetMap does — every style layer contributes.
    /// </summary>
    private static bool TryParseLegendScale(IQueryCollection query, out double? zoom, out string error)
    {
        zoom = null;
        error = string.Empty;

        var raw = GetQueryValue(query, "SCALE");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) ||
            scale <= 0 || double.IsInfinity(scale))
        {
            error = "SCALE must be a positive scale denominator.";
            return false;
        }

        zoom = Math.Log2(LegendScaleDenominatorZoom0 / scale);
        return true;
    }
}
