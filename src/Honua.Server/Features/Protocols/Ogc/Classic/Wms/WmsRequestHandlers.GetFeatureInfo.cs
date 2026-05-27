// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Protocols.Ogc.Classic;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Server.Features.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wms;

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

        if (!IsExtentWithinCrsBounds(requestedExtent, normalizedCrs))
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
        foreach (var layer in queryLayers)
        {
            if (!mapLayerStorageIds.Contains(layer.StorageLayerId))
            {
                return CreateWmsServiceException(context, "LayerNotDefined", "QUERY_LAYERS must be a subset of LAYERS.");
            }
        }

        if (!TryNormalizeFeatureInfoFormat(GetQueryValue(query, "INFO_FORMAT"), out var infoFormat))
        {
            return CreateWmsServiceException(context, "InvalidFormat", "Unsupported INFO_FORMAT. Supported values are text/plain and application/json.");
        }

        var filterResult = TryParseWmsLayerFilters(context, query, mapLayers);
        if (filterResult.Error != null)
        {
            return filterResult.Error;
        }
        var filtersByStorageLayerId = filterResult.Filters is null
            ? null
            : mapLayers
                .Select((layer, index) => (layer.StorageLayerId, Filter: filterResult.Filters[index]))
                .Where(item => item.Filter is not null)
                .ToDictionary(item => item.StorageLayerId, item => item.Filter);

        var featureCount = DefaultFeatureInfoCount;
        var featureCountRaw = GetQueryValue(query, "FEATURE_COUNT");
        if (!string.IsNullOrWhiteSpace(featureCountRaw))
        {
            if (!int.TryParse(featureCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureCount) || featureCount <= 0)
            {
                return CreateWmsServiceException(context, "InvalidParameterValue", "FEATURE_COUNT must be a positive integer.");
            }
        }

        var mapWidth = requestedExtent.MaxX - requestedExtent.MinX;
        var mapHeight = requestedExtent.MaxY - requestedExtent.MinY;
        var mapX = requestedExtent.MinX + (((pixelX + 0.5) / imageWidth) * mapWidth);
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

        var body = plainText.Length > 0
            ? plainText.ToString().TrimEnd()
            : "No features found.";
        return Results.Content(body, PlainTextMimeType, Encoding.UTF8, StatusCodes.Status200OK);
    }
}
