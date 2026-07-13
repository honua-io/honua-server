// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.Rendering;
using Honua.Protocols.Ogc.Classic;
using Honua.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wms;

internal static partial class WmsRequestHandlers
{
    private static async Task<IResult> HandleWmsGetFeatureInfo(
        HttpContext context,
        MetadataV2Service service,
        WmsLayer[] accessibleLayers,
        string serviceId,
        ILogger logger)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        OgcClassicLog.WmsRequested(logger, serviceId, "GetFeatureInfo");
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureIdentify, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcMaps);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wms-getfeatureinfo");

        var query = context.Request.Query;

        if (!TryGetRequiredQueryValue(query, "LAYERS", out var layersParam) ||
            !TryParseCsvTokens(layersParam, allowEmptyTokens: false, out var mapLayerTokens))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "LAYERS parameter is required.");
        }

        if (!TryGetRequiredQueryValue(query, "QUERY_LAYERS", out var queryLayersParam) ||
            !TryParseCsvTokens(queryLayersParam, allowEmptyTokens: false, out var queryLayerTokens))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "QUERY_LAYERS parameter is required.");
        }

        if (!TryGetRequiredQueryValue(query, "BBOX", out var bboxValue))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Invalid BBOX parameter. Expected format: xmin,ymin,xmax,ymax.");
        }

        if (!TryGetRequiredQueryValue(query, "VERSION", out var versionValue))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "VERSION parameter is required.");
        }

        if (!IsSupportedWmsVersion(versionValue))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Unsupported VERSION value. Supported values are 1.3.0 and 1.1.1.");
        }

        var crsValue = GetQueryValue(query, "CRS");
        if (string.IsNullOrWhiteSpace(crsValue))
        {
            crsValue = GetQueryValue(query, "SRS");
        }

        if (!TryParseWmsCrs(crsValue, out var requestSrid, out var normalizedCrs))
        {
            return CreateWmsServiceException(context, "InvalidCRS", "Invalid or missing CRS/SRS parameter.");
        }

        if (!TryParseWmsBbox(bboxValue, normalizedCrs, versionValue, out var requestedExtent))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Invalid BBOX parameter. Expected format: xmin,ymin,xmax,ymax.");
        }

        // BH6-010: Use the same lenient intersection check GetMap uses rather than the
        // strict containment check. A BBOX that extends outside CRS bounds but still
        // intersects it renders fine in GetMap; GetFeatureInfo must not reject it with 400
        // when GetMap would succeed. Any out-of-bounds click geometry is clamped during
        // feature query rather than rejected at the validation gate.
        if (!DoesExtentIntersectCrsBounds(requestedExtent, normalizedCrs))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "BBOX is outside the valid range for the requested CRS.");
        }

        if (!TryGetRequiredQueryValue(query, "WIDTH", out var widthValue) ||
            !int.TryParse(widthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageWidth) ||
            imageWidth <= 0 || imageWidth > WmsMaxImageDimension)
        {
            return CreateWmsServiceException(context, "InvalidDimensionValue", $"WIDTH must be an integer between 1 and {WmsMaxImageDimension.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!TryGetRequiredQueryValue(query, "HEIGHT", out var heightValue) ||
            !int.TryParse(heightValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageHeight) ||
            imageHeight <= 0 || imageHeight > WmsMaxImageDimension)
        {
            return CreateWmsServiceException(context, "InvalidDimensionValue", $"HEIGHT must be an integer between 1 and {WmsMaxImageDimension.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!TryParseWmsFeatureInfoPixel(query, versionValue, imageWidth, imageHeight, out var pixelX, out var pixelY))
        {
            var pointParameters = IsWms111Version(versionValue) ? "X/Y" : "I/J";
            return CreateWmsServiceException(context, "InvalidPoint", $"{pointParameters} must be within the request image dimensions.");
        }

        if (!TryResolveWmsRequestedLayers(accessibleLayers, mapLayerTokens, out var mapLayers, out var unresolvedMapLayer))
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedMapLayer) ? "requested layer" : unresolvedMapLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        if (!TryResolveWmsRequestedLayers(accessibleLayers, queryLayerTokens, out var queryLayers, out var unresolvedQueryLayer))
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedQueryLayer) ? "requested layer" : unresolvedQueryLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        var mapLayerStorageIds = new HashSet<int>(mapLayers.Select(layer => layer.StorageLayerId));
        if (queryLayers.Any(layer => !mapLayerStorageIds.Contains(layer.StorageLayerId)))
        {
            return CreateWmsServiceException(context, "LayerNotDefined", "QUERY_LAYERS must be a subset of LAYERS.");
        }

        if (!TryNormalizeFeatureInfoFormat(GetQueryValue(query, "INFO_FORMAT"), out var infoFormat))
        {
            return CreateWmsServiceException(context, "InvalidFormat", "Unsupported INFO_FORMAT. Supported values are text/plain, application/vnd.ogc.gml, and application/json.");
        }

        var filterResult = TryParseWmsLayerFilters(context, query, mapLayers);
        if (filterResult.Error != null)
        {
            return filterResult.Error;
        }
        // BH5-007: use GroupBy before ToDictionary to handle duplicate LAYERS tokens (e.g.
        // LAYERS=X,X) without throwing ArgumentException on the duplicate StorageLayerId key.
        // The temporal filter path below has always used this pattern; the spatial path is now
        // consistent with it.
        var filtersByStorageLayerId = filterResult.Filters is null
            ? null
            : mapLayers
                .Select((layer, index) => (layer.StorageLayerId, Filter: filterResult.Filters[index]))
                .Where(item => item.Filter is not null)
                .GroupBy(item => item.StorageLayerId)
                .ToDictionary(group => group.Key, group => group.First().Filter);

        // Apply the TIME dimension to identify, mirroring GetMap. Previously GetFeatureInfo
        // ignored TIME entirely, so on a time-enabled layer GetMap showed the selected instant
        // but identify returned features across all times (and an invalid TIME was silently
        // accepted instead of InvalidDimensionValue) (#1991). Parsed against queryLayers so
        // each queried layer gets its own temporal filter slot.
        var temporalResult = TryParseWmsLayerTemporalFilters(context, query, queryLayers);
        if (temporalResult.Error != null)
        {
            return temporalResult.Error;
        }

        var temporalFiltersByStorageLayerId = temporalResult.Filters is null
            ? null
            : queryLayers
                .Select((layer, index) => (layer.StorageLayerId, Filter: temporalResult.Filters[index]))
                .Where(item => item.Filter is not null)
                .GroupBy(item => item.StorageLayerId)
                .ToDictionary(group => group.Key, group => group.First().Filter);

        var featureCount = DefaultFeatureInfoCount;
        var featureCountRaw = GetQueryValue(query, "FEATURE_COUNT");
        if (!string.IsNullOrWhiteSpace(featureCountRaw) &&
            (!int.TryParse(featureCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureCount) || featureCount <= 0))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "FEATURE_COUNT must be a positive integer.");
        }

        // Use the wrap-aware effective width so a dateline-crossing geographic BBOX (minX > maxX,
        // which GetMap supports via BuildTransform/GetEffectiveWidth) maps pixels to coordinates
        // correctly instead of producing a negative width, a mirrored click point, and a negative
        // tolerance (#2739). The inverse of GetMap's NormalizeLongitude can land past the +limit
        // edge, so fold it back into the canonical world range.
        var mapWidth = CoordinateTransformer.GetEffectiveWidth(requestedExtent);
        var mapHeight = requestedExtent.MaxY - requestedExtent.MinY;
        var mapX = ComputeGetFeatureInfoMapX(requestedExtent, pixelX, imageWidth);
        var mapY = requestedExtent.MaxY - (((pixelY + 0.5) / imageHeight) * mapHeight);

        var toleranceX = Math.Max((mapWidth / imageWidth) * DefaultFeatureInfoTolerancePixels, 0.000001);
        var toleranceY = Math.Max((mapHeight / imageHeight) * DefaultFeatureInfoTolerancePixels, 0.000001);
        var clickExtent = new SkiaMapRenderer.RenderExtent(
            mapX - toleranceX,
            mapY - toleranceY,
            mapX + toleranceX,
            mapY + toleranceY);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var remaining = Math.Min(featureCount, 1000);

        var plainText = new StringBuilder();
        var jsonFeatures = new List<WmsFeatureInfoFeature>();
        var gmlFeatures = new List<WmsFeatureInfoFeature>();

        foreach (var layer in queryLayers)
        {
            if (remaining <= 0)
            {
                break;
            }

            // Per-layer source SRID — see ResolveServiceSrid for the resolution order.
            var serviceSrid = ResolveServiceSrid(service, layer.Resource);

            var layerClickExtent = clickExtent;
            if (requestSrid != serviceSrid)
            {
                var clickExtentTransform = await TryTransformExtentAsync(
                    context,
                    clickExtent,
                    requestSrid,
                    serviceSrid,
                    cancellationToken);
                if (!clickExtentTransform.IsSuccess)
                {
                    return CreateWmsServiceException(context, "InvalidCRS", clickExtentTransform.Error ?? "Invalid spatial reference.");
                }

                layerClickExtent = clickExtentTransform.Extent;
            }

            var spatialFilter = CreateBboxSpatialFilter(layerClickExtent, serviceSrid);

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SqlFilter = filtersByStorageLayerId != null && filtersByStorageLayerId.TryGetValue(layer.StorageLayerId, out var sqlFilter)
                    ? sqlFilter
                    : null,
                TemporalFilter = temporalFiltersByStorageLayerId != null
                    && temporalFiltersByStorageLayerId.TryGetValue(layer.StorageLayerId, out var temporalFilter)
                    ? temporalFilter
                    : null,
                SpatialReferenceSrid = serviceSrid,
                OutputSrid = requestSrid,
                Limit = remaining
            };

            // IFeatureReader.QueryAsync is keyed by the int storage layer handle. V2
            // resolves that off the publication; passing publication.LayerIndex here
            // would address the wrong table when the protocol-facing index differs
            // from the storage handle (the common case once a graph has migrated its
            // bindings). Matches the resolution order the WMTS V2 port uses.
            var queryResult = await featureReader.QueryAsync(layer.StorageLayerId, featureQuery, cancellationToken);
            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            foreach (var item in queryResult.Items)
            {
                if (remaining <= 0)
                {
                    break;
                }

                remaining--;
                var layerName = GetWmsLayerDisplayName(layer);
                var attributes = BuildVisibleFeatureInfoAttributes(item);

                if (string.Equals(infoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase))
                {
                    jsonFeatures.Add(new WmsFeatureInfoFeature
                    {
                        Layer = layerName,
                        Attributes = attributes
                    });
                    continue;
                }

                if (string.Equals(infoFormat, GmlFeatureInfoMimeType, StringComparison.OrdinalIgnoreCase))
                {
                    gmlFeatures.Add(new WmsFeatureInfoFeature
                    {
                        Layer = layerName,
                        Attributes = attributes
                    });
                    continue;
                }

                plainText.Append("Layer=").Append(layerName).AppendLine();
                foreach (var attribute in attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    plainText.Append(attribute.Key)
                        .Append('=')
                        .Append(FormatFeatureInfoValue(attribute.Value))
                        .AppendLine();
                }

                plainText.AppendLine();
            }
        }

        if (string.Equals(infoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            var payload = new WmsFeatureInfoResponse
            {
                Features = [.. jsonFeatures]
            };

            return Results.Json(payload, OgcClassicJsonContext.Default.WmsFeatureInfoResponse, contentType: JsonMimeType);
        }

        if (string.Equals(infoFormat, GmlFeatureInfoMimeType, StringComparison.OrdinalIgnoreCase))
        {
            var gml = BuildWmsGmlFeatureInfo(gmlFeatures);
            return Results.Content(gml, GmlFeatureInfoMimeType, Encoding.UTF8, StatusCodes.Status200OK);
        }

        var body = plainText.Length > 0
            ? plainText.ToString().TrimEnd()
            : "No features found.";
        return Results.Content(body, PlainTextMimeType, Encoding.UTF8, StatusCodes.Status200OK);
    }

    /// <summary>
    /// Maps a horizontal pixel column to its map X coordinate for GetFeatureInfo, using the
    /// wrap-aware effective width so a dateline-crossing geographic BBOX (which GetMap supports)
    /// inverts correctly and folds a result past the world's positive edge back into range (#2739).
    /// </summary>
    internal static double ComputeGetFeatureInfoMapX(SkiaMapRenderer.RenderExtent extent, double pixelX, int imageWidth)
    {
        var effectiveWidth = CoordinateTransformer.GetEffectiveWidth(extent);
        var normalizedMapX = extent.MinX + (((pixelX + 0.5) / imageWidth) * effectiveWidth);
        return FoldWrappedLongitude(normalizedMapX, extent);
    }

    /// <summary>
    /// Folds a longitude that the inverse pixel-to-map math produced past the world's positive
    /// edge back into the canonical world range for a wrapped (antimeridian-crossing) extent.
    /// Mirrors <c>CoordinateTransformer.NormalizeLongitude</c>, which folds the opposite direction
    /// in the forward map-to-pixel transform. Non-wrapped extents are returned unchanged (#2739).
    /// </summary>
    private static double FoldWrappedLongitude(double value, SkiaMapRenderer.RenderExtent extent)
    {
        if (CoordinateTransformer.TryGetWrappedWorldWidth(extent, out var worldWidth))
        {
            var halfWidth = worldWidth / 2.0;
            if (value > halfWidth)
            {
                value -= worldWidth;
            }
        }

        return value;
    }

    /// <summary>
    /// Builds a WMS GetFeatureInfo GML response (INFO_FORMAT
    /// <c>application/vnd.ogc.gml</c>). Emits an OGC FeatureInfoResponse wrapping
    /// one element per identified feature with its visible attributes as child
    /// elements. This mirrors the MapServer/GeoServer <c>msGMLOutput</c> shape the
    /// WMS 1.1.1 GML FeatureInfo conformance class (ets-wms11
    /// <c>wms:wms-getfeatureinfo</c>) expects: a well-formed XML document whose
    /// content type matches the advertised GML format.
    /// </summary>
    private static string BuildWmsGmlFeatureInfo(IReadOnlyList<WmsFeatureInfoFeature> features)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<msGMLOutput xmlns:gml=\"http://www.opengis.net/gml\" ")
            .AppendLine("xmlns:xlink=\"http://www.w3.org/1999/xlink\">");

        foreach (var feature in features)
        {
            var layerElement = ToGmlElementName(feature.Layer);
            sb.Append("  <").Append(layerElement).Append("_layer>").AppendLine();
            sb.Append("    <").Append(layerElement).Append("_feature>").AppendLine();
            sb.Append("      <gml:name>")
                .Append(EscapeXml(feature.Layer))
                .AppendLine("</gml:name>");

            foreach (var attribute in feature.Attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var element = ToGmlElementName(attribute.Key);
                sb.Append("      <")
                    .Append(element)
                    .Append('>')
                    .Append(EscapeXml(FormatFeatureInfoValue(attribute.Value)))
                    .Append("</")
                    .Append(element)
                    .AppendLine(">");
            }

            sb.Append("    </").Append(layerElement).Append("_feature>").AppendLine();
            sb.Append("  </").Append(layerElement).Append("_layer>").AppendLine();
        }

        sb.AppendLine("</msGMLOutput>");
        return sb.ToString();
    }

    /// <summary>
    /// Normalizes an arbitrary layer/attribute name into a safe XML element
    /// NCName: strips a namespace prefix, replaces non name-char runs with '_',
    /// and prefixes a leading non-letter so the result is always a valid element
    /// name. Falls back to <c>field</c> when nothing usable remains.
    /// </summary>
    private static string ToGmlElementName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "field";
        }

        var colon = name.LastIndexOf(':');
        var local = colon >= 0 && colon < name.Length - 1 ? name[(colon + 1)..] : name;

        var sb = new StringBuilder(local.Length);
        foreach (var ch in local)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' ? ch : '_');
        }

        var candidate = sb.ToString().Trim('_');
        if (candidate.Length == 0)
        {
            return "field";
        }

        if (!char.IsLetter(candidate[0]) && candidate[0] != '_')
        {
            candidate = "_" + candidate;
        }

        return candidate;
    }
}
