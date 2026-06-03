// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using static Honua.Infrastructure.Helpers.DelimitedParameterHelpers;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const int DefaultTolerance = 3;
    private const int MaxIdentifyGeometryInputLength = 2000;
    private const string InvalidIdentifyRequestMessage = "Invalid identify request parameters.";
    private const string InvalidIdentifyGeometryMessage = "Geometry parameter is invalid.";

    private enum IdentifyLayerMode
    {
        Top,
        Visible,
        All
    }

    private enum IdentifyGeometryKind
    {
        Point,
        MultiPoint,
        Polyline,
        Polygon,
        Envelope
    }

    private sealed record IdentifyLayerSelection(
        IdentifyLayerMode Mode,
        IdentifyRenderLayer[] RenderLayers,
        bool TopLayerFirst);

    private sealed record IdentifyLayerDescriptor(
        int PublicLayerId,
        int StorageLayerId,
        string Name,
        MetadataV2Resource Resource);

    private sealed record IdentifyRenderLayer(
        IdentifyLayerDescriptor Layer,
        int Id,
        string? DefinitionExpression);

    private sealed record IdentifyGeometryInput(
        string RawValue,
        bool IsJson,
        double? X,
        double? Y,
        double? Xmin,
        double? Ymin,
        double? Xmax,
        double? Ymax,
        bool HasPoints,
        bool HasPaths,
        bool HasRings,
        int? Wkid,
        int? LatestWkid);

    /// <summary>
    /// Handle MapServer identify (click-query) requests.
    /// </summary>
    private static async Task<IResult> HandleIdentify(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");
        using var scope = HonuaTelemetryScope.StartFeature(
            "identify",
            HonuaTelemetry.Protocols.MapServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "identify");

        try
        {
            var earlyServiceError = await TryValidateMapServerServiceAsync(serviceId, context);
            if (earlyServiceError is not null)
            {
                return earlyServiceError;
            }

            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var geometryValue = GetValue(values, "geometry");
            if (!TryParseGeometryType(GetValue(values, "geometryType"), out var geometryTypeValue, out var geometryTypeError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, geometryTypeError ?? "Invalid geometryType parameter.");
            }

            var srValue = GetValue(values, "sr");
            var layersParam = GetValue(values, "layers");
            if (HasEmptyCommaSeparatedToken(layersParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter contains an empty layer id.");
            }

            if (HasNonIntegerIdentifyLayerToken(layersParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must contain integer layer ids.");
            }

            var layerDefsValue = GetValue(values, "layerDefs");
            if (!TryParseTolerance(GetValue(values, "tolerance"), out var tolerance, out var toleranceError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, toleranceError ?? "Invalid tolerance parameter.");
            }

            var mapExtentValue = GetValue(values, "mapExtent");
            var imageDisplayValue = GetValue(values, "imageDisplay");
            var returnGeometry = !string.Equals(GetValue(values, "returnGeometry"), "false", StringComparison.OrdinalIgnoreCase);
            var responseFormat = GetValue(values, "f") ?? "json";

            if (!string.Equals(responseFormat, "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(responseFormat, "pjson", StringComparison.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Output format '{responseFormat}' is not supported.");
            }

            if (string.IsNullOrWhiteSpace(geometryValue))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Geometry parameter is required.");
            }

            if (string.IsNullOrWhiteSpace(mapExtentValue) || string.IsNullOrWhiteSpace(imageDisplayValue))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "mapExtent and imageDisplay parameters are required.");
            }

            if (!TryParseIdentifyGeometry(
                    geometryValue,
                    geometryTypeValue,
                    out var geometry,
                    out var geometryError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, geometryError ?? "Invalid geometry parameter.");
            }

            if (!TryParseImageDisplay(imageDisplayValue, out var imageWidth, out var imageHeight, out var imageDpi, out var imageDisplayError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, imageDisplayError ?? "Invalid imageDisplay parameter.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveIdentifyPublishedLayers(snapshot, service);

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            if (!TryParseLayerDefs(layerDefsValue, queryValidator, out var layerDefs, out var layerDefsError))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    layerDefsError ?? "Invalid layerDefs parameter.");
            }

            if (!TryParseLayerTimeOptions(GetValue(values, "layerTimeOptions"), out var layerTimeOptions, out var layerTimeOptionsError))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    layerTimeOptionsError ?? "Invalid layerTimeOptions parameter.");
            }

            var knownLayerIds = publishedLayers
                .Select(static layer => layer.PublicLayerId)
                .ToHashSet();
            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), knownLayerIds, queryValidator, out var dynamicLayers, out var dynamicLayersError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    dynamicLayersError ?? "Invalid dynamicLayers parameter.");
            }

            scope.WithTag(
                HonuaTelemetry.Tags.LayerId,
                ResolveIdentifyTelemetryLayerTag(layersParam, dynamicLayers.Count > 0));

            var timeValue = GetValue(values, "time");
            var timeRelationValue = NormalizeTimeRelation(GetValue(values, "timeRelation"));
            var serviceSrid = ResolveIdentifyServiceSrid(service, publishedLayers);

            if (!TryResolveIdentifySrid(srValue, geometry, serviceSrid, out var geometrySrid, out var srError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, srError ?? "Invalid spatial reference.");
            }

            var mapExtentCrsDefinition = SpatialReferenceHelpers.TryParseCrsDefinition(
                srValue ?? geometrySrid.ToString(CultureInfo.InvariantCulture),
                out var resolvedMapExtentDefinition)
                ? resolvedMapExtentDefinition
                : new CrsDefinition(
                    string.Empty,
                    geometrySrid,
                    AxisOrder.EastNorth,
                    SpatialReference.Create(geometrySrid).IsGeographic);
            // ArcGIS JS SDK and arcpy send mapExtent as an Esri JSON envelope
            // ({"xmin":..,"ymin":..,"xmax":..,"ymax":..,"spatialReference":..}).
            // Normalize that to the comma form before the shared bbox parser, which
            // also keeps the existing "xmin,ymin,xmax,ymax" string form working.
            if (!TryNormalizeIdentifyMapExtent(mapExtentValue, out var normalizedMapExtent, out var mapExtentError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, mapExtentError ?? "Invalid mapExtent parameter. Expected format: xmin,ymin,xmax,ymax");
            }

            if (!TryParseBbox(
                    normalizedMapExtent,
                    mapExtentCrsDefinition.AxisOrder,
                    mapExtentCrsDefinition.IsGeographic,
                    out var mapExtent))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid mapExtent parameter. Expected format: xmin,ymin,xmax,ymax");
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                if (TryGetIdentifyPoint(geometry, out var pointX, out var pointY))
                {
                    var pointXString = pointX.ToString(CultureInfo.InvariantCulture);
                    var pointYString = pointY.ToString(CultureInfo.InvariantCulture);
                    MapServerLog.IdentifyRequested(logger, serviceId, pointXString, pointYString);
                }
                else
                {
                    MapServerLog.IdentifyRequested(logger, serviceId, "n/a", "n/a");
                }
            }

            var stopwatch = Stopwatch.StartNew();

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var geometryConverter = context.RequestServices.GetRequiredService<IGeometryConverter>();
            var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
            var maxIdentifyResults = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query.MaxRecordCount;

            var scaleDenominator = CoordinateTransformer.CalculateScaleDenominator(mapExtent, imageWidth, imageDpi, geometrySrid);
            var identifyAccessCandidates = ResolveIdentifyAccessCandidateLayers(
                publishedLayers,
                layersParam,
                dynamicLayers,
                scaleDenominator);
            var identifyAccessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                identifyAccessCandidates.Select(static layer => layer.Resource),
                service);
            if (identifyAccessError != null)
            {
                return identifyAccessError;
            }

            var identifySelection = ResolveIdentifyLayers(publishedLayers, service, layersParam, dynamicLayers, context, scaleDenominator);

            var searchTolerance = CoordinateTransformer.PixelToMapUnits(tolerance, mapExtent, imageWidth);
            if (!TryBuildIdentifySpatialFilter(
                    geometry,
                    geometryTypeValue,
                    searchTolerance,
                    geometrySrid,
                    geometryConverter,
                    out var spatialFilter,
                    out var spatialError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, spatialError ?? "Invalid geometry parameter.");
            }

            var results = new List<IdentifyResult>();

            var layersToSearch = identifySelection.Mode == IdentifyLayerMode.Top
                ? (identifySelection.TopLayerFirst
                    ? identifySelection.RenderLayers
                    : identifySelection.RenderLayers.Reverse().ToArray())
                : identifySelection.RenderLayers;

            foreach (var renderLayer in layersToSearch)
            {
                if (results.Count >= maxIdentifyResults)
                {
                    break;
                }

                var layer = renderLayer.Layer;
                if (!HasIdentifyGeometry(layer.Resource))
                {
                    continue;
                }

                layerDefs.TryGetValue(layer.PublicLayerId, out var layerDef);
                var combinedDefinition = CombineDefinitionExpressions(renderLayer.DefinitionExpression, layerDef);

                if (!TryGetEffectiveTimeParameters(
                        timeValue,
                        timeRelationValue,
                        renderLayer,
                        layerTimeOptions,
                        out var effectiveTime,
                        out var effectiveTimeRelation,
                        out var timeError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        timeError ?? "Invalid time parameter.");
                }

                if (!TryBuildLayerSqlFilter(
                        filterExpressionService,
                        layer.Resource,
                        combinedDefinition,
                        effectiveTime,
                        effectiveTimeRelation,
                        out var sqlFilter,
                        out var filterError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        filterError ?? "Invalid filter parameter.");
                }

                var remainingResults = maxIdentifyResults - results.Count;
                if (remainingResults <= 0)
                {
                    break;
                }

                var featureQuery = new FeatureQuery
                {
                    SpatialFilter = spatialFilter,
                    SpatialReferenceSrid = layer.Resource.ReadSrid() ?? SpatialReference.WGS84.Wkid,
                    OutputSrid = geometrySrid,
                    Limit = remainingResults,
                    SqlFilter = sqlFilter
                };

                var queryResult = await featureReader.QueryAsync(layer.StorageLayerId, featureQuery, cancellationToken);

                if (queryResult.Items.Length == 0)
                {
                    continue;
                }

                var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(layer.Resource);
                var displayField = ResolveDisplayField(layer.Resource, objectIdField);

                foreach (var feature in queryResult.Items)
                {
                    var attributes = new Dictionary<string, object?>();
                    foreach (var kvp in feature.Attributes)
                    {
                        if (FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                        {
                            continue;
                        }

                        attributes[kvp.Key] = FeatureAttributeValueNormalizer.Normalize(kvp.Value);
                    }

                    object? geometryResult = null;
                    if (returnGeometry && feature.Geometry != null)
                    {
                        try
                        {
                            geometryResult = geometryConverter.ConvertWkbToGeoServicesGeometry(feature.Geometry, geometrySrid);
                        }
                        catch (ArgumentException)
                        {
                            geometryResult = null;
                        }
                    }

                    var displayValue = GetDisplayFieldValue(feature, displayField);

                    var identifyResult = new IdentifyResult
                    {
                        LayerId = layer.PublicLayerId,
                        LayerName = layer.Name,
                        DisplayFieldName = displayField,
                        Value = displayValue,
                        Attributes = attributes,
                        GeometryType = MapGeometryTypeToEsri(layer.Resource.ReadGeometryType()),
                        Geometry = geometryResult
                    };

                    results.Add(identifyResult);
                    if (results.Count >= maxIdentifyResults)
                    {
                        break;
                    }
                }

                if ((identifySelection.Mode == IdentifyLayerMode.Top && results.Count > 0) ||
                    results.Count >= maxIdentifyResults)
                {
                    break;
                }
            }

            MapServerLog.IdentifyCompleted(logger, serviceId, results.Count);
            stopwatch.Stop();
            scope.SetSuccess(results.Count);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            var response = new IdentifyResponse { Results = [.. results] };
            return Results.Json(response, MapServerJsonContext.Default.IdentifyResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            MapServerLog.IdentifyFailed(logger, serviceId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateBadRequest(context, InvalidIdentifyRequestMessage);
        }
        catch (Exception ex)
        {
            MapServerLog.IdentifyFailed(logger, serviceId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer identify failed.");
        }
    }

    private static bool TryParseImageDisplay(
        string imageDisplay,
        out int width,
        out int height,
        out int dpi,
        out string? error)
    {
        width = 0;
        height = 0;
        dpi = 0;
        error = null;

        var parts = imageDisplay.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            error = "Invalid imageDisplay parameter. Expected format: width,height,dpi";
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
        {
            error = "Invalid imageDisplay width.";
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0)
        {
            error = "Invalid imageDisplay height.";
            return false;
        }

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out dpi) || dpi <= 0)
        {
            error = "Invalid imageDisplay dpi.";
            return false;
        }

        return true;
    }

    private static bool TryParseTolerance(string? rawTolerance, out int tolerance, out string? error)
    {
        error = null;
        tolerance = DefaultTolerance;

        if (string.IsNullOrWhiteSpace(rawTolerance))
        {
            return true;
        }

        if (!int.TryParse(rawTolerance, NumberStyles.Integer, CultureInfo.InvariantCulture, out tolerance))
        {
            error = "Invalid tolerance parameter.";
            return false;
        }

        if (tolerance < 0)
        {
            error = "tolerance cannot be negative.";
            return false;
        }

        return true;
    }

    private static bool TryParseGeometryType(string? rawGeometryType, out string geometryType, out string? error)
    {
        error = null;
        geometryType = "esriGeometryPoint";

        if (string.IsNullOrWhiteSpace(rawGeometryType))
        {
            return true;
        }

        var normalized = rawGeometryType.Trim();
        if (string.Equals(normalized, "esriGeometryPoint", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "esriGeometryMultipoint", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "esriGeometryPolyline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "esriGeometryPolygon", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "esriGeometryEnvelope", StringComparison.OrdinalIgnoreCase))
        {
            geometryType = normalized;
            return true;
        }

        error = "Invalid geometryType parameter.";
        return false;
    }

    private static bool TryResolveIdentifySrid(
        string? srValue,
        IdentifyGeometryInput geometry,
        int serviceSrid,
        out int srid,
        out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(srValue))
        {
            var parsed = SpatialReferenceHelpers.TryParseSrid(srValue);
            if (!parsed.HasValue)
            {
                srid = serviceSrid;
                error = "Invalid sr parameter.";
                return false;
            }

            srid = parsed.Value;
            return true;
        }

        if (geometry.LatestWkid is { } latestWkid && latestWkid > 0)
        {
            srid = latestWkid;
            return true;
        }

        if (geometry.Wkid is { } wkid && wkid > 0)
        {
            srid = wkid;
            return true;
        }

        srid = serviceSrid;
        return true;
    }

    /// <summary>
    /// Normalizes a mapExtent value into the comma-delimited "xmin,ymin,xmax,ymax"
    /// form. Accepts both the legacy comma string and the Esri JSON envelope object
    /// ({"xmin":..,"ymin":..,"xmax":..,"ymax":..}) that the ArcGIS JS SDK and arcpy emit.
    /// </summary>
    private static bool TryNormalizeIdentifyMapExtent(
        string? mapExtentValue,
        out string? normalized,
        out string? error)
    {
        normalized = mapExtentValue;
        error = null;

        if (string.IsNullOrWhiteSpace(mapExtentValue))
        {
            return true;
        }

        var trimmed = mapExtentValue.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            // Legacy comma form (or anything else) — let the shared bbox parser decide.
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(mapExtentValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Invalid mapExtent parameter. Expected an Esri JSON envelope or xmin,ymin,xmax,ymax.";
                return false;
            }

            var root = doc.RootElement;
            var xmin = TryGetDouble(root, "xmin");
            var ymin = TryGetDouble(root, "ymin");
            var xmax = TryGetDouble(root, "xmax");
            var ymax = TryGetDouble(root, "ymax");

            if (!(xmin.HasValue && ymin.HasValue && xmax.HasValue && ymax.HasValue))
            {
                error = "mapExtent envelope must include xmin, ymin, xmax, and ymax.";
                return false;
            }

            normalized = string.Create(
                CultureInfo.InvariantCulture,
                $"{xmin.Value},{ymin.Value},{xmax.Value},{ymax.Value}");
            return true;
        }
        catch (JsonException)
        {
            error = "Invalid mapExtent parameter. Expected an Esri JSON envelope or xmin,ymin,xmax,ymax.";
            return false;
        }
    }

    private static bool TryParseIdentifyGeometry(
        string geometryValue,
        string geometryType,
        out IdentifyGeometryInput geometry,
        out string? error)
    {
        geometry = default!;
        error = null;

        if (geometryValue.Length > MaxIdentifyGeometryInputLength)
        {
            error = "Geometry parameter is too long.";
            return false;
        }

        if (string.Equals(geometryType, "esriGeometryPoint", StringComparison.OrdinalIgnoreCase) &&
            TryParsePointPair(geometryValue, out var pairX, out var pairY))
        {
            geometry = new IdentifyGeometryInput(
                geometryValue,
                IsJson: false,
                X: pairX,
                Y: pairY,
                Xmin: null,
                Ymin: null,
                Xmax: null,
                Ymax: null,
                HasPoints: false,
                HasPaths: false,
                HasRings: false,
                Wkid: null,
                LatestWkid: null);
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(geometryValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Geometry must be a JSON object.";
                return false;
            }

            var root = doc.RootElement;
            var x = TryGetDouble(root, "x");
            var y = TryGetDouble(root, "y");
            var xmin = TryGetDouble(root, "xmin");
            var ymin = TryGetDouble(root, "ymin");
            var xmax = TryGetDouble(root, "xmax");
            var ymax = TryGetDouble(root, "ymax");
            var hasPoints = HasNonEmptyArray(root, "points");
            var hasPaths = HasNonEmptyArray(root, "paths");
            var hasRings = HasNonEmptyArray(root, "rings");
            var (wkid, latestWkid) = TryGetSpatialReference(root);

            if (!(x.HasValue && y.HasValue) &&
                !(xmin.HasValue && ymin.HasValue && xmax.HasValue && ymax.HasValue) &&
                !hasPoints &&
                !hasPaths &&
                !hasRings)
            {
                error = "Geometry JSON does not contain a supported geometry shape.";
                return false;
            }

            geometry = new IdentifyGeometryInput(
                geometryValue,
                IsJson: true,
                X: x,
                Y: y,
                Xmin: xmin,
                Ymin: ymin,
                Xmax: xmax,
                Ymax: ymax,
                HasPoints: hasPoints,
                HasPaths: hasPaths,
                HasRings: hasRings,
                Wkid: wkid,
                LatestWkid: latestWkid);
            return true;
        }
        catch (JsonException)
        {
            error = "Geometry parameter must be valid GeoServices JSON or 'x,y'.";
            return false;
        }
    }

    private static bool TryParsePointPair(string value, out double x, out double y)
    {
        x = 0;
        y = 0;

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    private static double? TryGetDouble(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static bool HasNonEmptyArray(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Array &&
               value.GetArrayLength() > 0;
    }

    private static (int? Wkid, int? LatestWkid) TryGetSpatialReference(JsonElement obj)
    {
        if (!obj.TryGetProperty("spatialReference", out var spatialReference) ||
            spatialReference.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var wkid = TryGetInt(spatialReference, "wkid");
        var latestWkid = TryGetInt(spatialReference, "latestWkid");
        return (wkid, latestWkid);
    }

    private static int? TryGetInt(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static IdentifyLayerDescriptor[] ResolveIdentifyPublishedLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        var descriptors = new List<IdentifyLayerDescriptor>();
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            if (publication.LayerIndex is not int publicLayerId)
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = snapshot.ResolveStorageLayerId(publication)
                ?? snapshot.ResolveStorageLayerId(resource)
                ?? publicLayerId;
            var layerName = string.IsNullOrWhiteSpace(resource.Metadata.Name)
                ? publication.Metadata.Name
                : resource.Metadata.Name;

            descriptors.Add(new IdentifyLayerDescriptor(
                publicLayerId,
                storageLayerId,
                string.IsNullOrWhiteSpace(layerName)
                    ? publicLayerId.ToString(CultureInfo.InvariantCulture)
                    : layerName,
                resource));
        }

        return [.. descriptors.OrderBy(static layer => layer.PublicLayerId)];
    }

    private static int ResolveIdentifyServiceSrid(
        MetadataV2Service service,
        IReadOnlyList<IdentifyLayerDescriptor> layers)
        => service.SpatialReference?.ResolveSrid()
            ?? layers.Select(static layer => layer.Resource.ReadSrid()).FirstOrDefault(static srid => srid.HasValue)
            ?? SpatialReference.WGS84.Wkid;

    private static bool HasIdentifyGeometry(MetadataV2Resource resource)
    {
        var geometryType = resource.ReadGeometryType();
        return geometryType != MetadataV2GeometryType.None ||
               resource.FindPrimaryGeometryField() is not null;
    }

    private static bool IsLayerVisibleAtScale(IdentifyLayerDescriptor layer, double scaleDenominator)
    {
        if (scaleDenominator <= 0)
        {
            return true;
        }

        var display = layer.Resource.Display;
        if (display?.MinScale is > 0 && scaleDenominator > display.MinScale.Value)
        {
            return false;
        }

        if (display?.MaxScale is > 0 && scaleDenominator < display.MaxScale.Value)
        {
            return false;
        }

        return true;
    }

    private static bool TryGetEffectiveTimeParameters(
        string? globalTime,
        string? globalTimeRelation,
        IdentifyRenderLayer renderLayer,
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        out string? effectiveTime,
        out string? effectiveTimeRelation,
        out string? error)
    {
        effectiveTime = globalTime;
        effectiveTimeRelation = globalTimeRelation;
        error = null;

        LayerTimeOptions? options = null;
        if (TryResolveLayerTimeOptions(layerTimeOptions, renderLayer, out var resolvedOptions))
        {
            options = resolvedOptions;
            if (options.UseTime is false)
            {
                effectiveTime = null;
                effectiveTimeRelation = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(options.Time))
            {
                effectiveTime = options.Time;
            }

            if (!string.IsNullOrWhiteSpace(options.TimeRelation))
            {
                effectiveTimeRelation = options.TimeRelation;
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveTime))
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(effectiveTime, out var start, out var end))
        {
            error = InvalidTimeParameterMessage;
            return false;
        }

        if (start is null && end is null && options?.TimeDataCumulative != true)
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (options?.TimeDataCumulative == true)
        {
            start = null;
        }

        if (options?.TimeOffset.HasValue == true)
        {
            if (!TryApplyTimeOffset(
                    start,
                    end,
                    options.TimeOffset.Value,
                    options.TimeOffsetUnits,
                    out var adjustedStart,
                    out var adjustedEnd,
                    out var offsetError))
            {
                error = offsetError ?? "Invalid time offset.";
                return false;
            }

            start = adjustedStart;
            end = adjustedEnd;
        }

        effectiveTime = BuildTimeParameter(start, end);
        effectiveTimeRelation = NormalizeTimeRelation(effectiveTimeRelation);
        return true;
    }

    private static bool TryResolveLayerTimeOptions(
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        IdentifyRenderLayer renderLayer,
        out LayerTimeOptions resolvedOptions)
    {
        if (layerTimeOptions.TryGetValue(renderLayer.Id, out var directOptions) &&
            directOptions is not null)
        {
            resolvedOptions = directOptions;
            return true;
        }

        if (renderLayer.Id != renderLayer.Layer.PublicLayerId &&
            layerTimeOptions.TryGetValue(renderLayer.Layer.PublicLayerId, out var sourceLayerOptions) &&
            sourceLayerOptions is not null)
        {
            resolvedOptions = sourceLayerOptions;
            return true;
        }

        resolvedOptions = default!;
        return false;
    }

    private static IdentifyLayerSelection ResolveIdentifyLayers(
        IReadOnlyList<IdentifyLayerDescriptor> publishedLayers,
        MetadataV2Service service,
        string? layersParam,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers,
        HttpContext context,
        double scaleDenominator)
    {
        var accessibleLayers = publishedLayers
            .Where(l => AccessPolicyHelpers.IsResourceAccessible(context, l.Resource, service))
            .ToArray();

        IdentifyLayerMode mode = IdentifyLayerMode.Top;
        var hasExplicitMode = false;
        HashSet<int>? ids = null;
        var excludeIds = false;

        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            var spec = layersParam.Trim();
            string? modeToken = null;
            string? idPart = null;

            var colonIndex = spec.IndexOf(':');
            if (colonIndex >= 0)
            {
                modeToken = spec[..colonIndex];
                idPart = spec[(colonIndex + 1)..];
            }
            else if (IsIdentifyLayerModeToken(spec))
            {
                modeToken = spec;
            }
            else
            {
                idPart = spec;
            }

            (mode, excludeIds) = ResolveIdentifyLayerMode(modeToken);
            hasExplicitMode = modeToken != null;

            if (!string.IsNullOrWhiteSpace(idPart))
            {
                ids = ParseLayerIds(idPart);
            }
        }

        if (dynamicLayers.Count > 0)
        {
            var layerLookup = publishedLayers.ToDictionary(static layer => layer.PublicLayerId);
            var renderLayers = new List<IdentifyRenderLayer>();

            foreach (var dl in dynamicLayers)
            {
                if (!layerLookup.TryGetValue(dl.MapLayerId, out var layer))
                {
                    continue;
                }

                if (!AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                {
                    continue;
                }

                renderLayers.Add(new IdentifyRenderLayer(layer, dl.Id, dl.DefinitionExpression));
            }

            var effectiveMode = hasExplicitMode && mode == IdentifyLayerMode.Top
                ? IdentifyLayerMode.Top
                : IdentifyLayerMode.All;

            return new IdentifyLayerSelection(effectiveMode, renderLayers.ToArray(), TopLayerFirst: true);
        }

        IEnumerable<IdentifyLayerDescriptor> candidates = accessibleLayers;

        if (mode is IdentifyLayerMode.Visible or IdentifyLayerMode.Top)
        {
            candidates = candidates.Where(l => l.Resource.Display?.DefaultVisibility ?? true);
            if (scaleDenominator > 0)
            {
                candidates = candidates.Where(l => IsLayerVisibleAtScale(l, scaleDenominator));
            }
        }

        if (ids is { Count: > 0 })
        {
            candidates = excludeIds
                ? candidates.Where(l => !ids.Contains(l.PublicLayerId))
                : candidates.Where(l => ids.Contains(l.PublicLayerId));
        }

        return new IdentifyLayerSelection(
            mode,
            candidates.Select(l => new IdentifyRenderLayer(l, l.PublicLayerId, null)).ToArray(),
            TopLayerFirst: false);
    }

    private static IdentifyLayerDescriptor[] ResolveIdentifyAccessCandidateLayers(
        IReadOnlyList<IdentifyLayerDescriptor> publishedLayers,
        string? layersParam,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers,
        double scaleDenominator)
    {
        IdentifyLayerMode mode = IdentifyLayerMode.Top;
        HashSet<int>? ids = null;
        var excludeIds = false;

        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            var spec = layersParam.Trim();
            string? modeToken = null;
            string? idPart = null;

            var colonIndex = spec.IndexOf(':');
            if (colonIndex >= 0)
            {
                modeToken = spec[..colonIndex];
                idPart = spec[(colonIndex + 1)..];
            }
            else if (IsIdentifyLayerModeToken(spec))
            {
                modeToken = spec;
            }
            else
            {
                idPart = spec;
            }

            (mode, excludeIds) = ResolveIdentifyLayerMode(modeToken);

            if (!string.IsNullOrWhiteSpace(idPart))
            {
                ids = ParseLayerIds(idPart);
            }
        }

        if (dynamicLayers.Count > 0)
        {
            var layerLookup = publishedLayers.ToDictionary(static layer => layer.PublicLayerId);
            return dynamicLayers
                .Select(dynamicLayer => layerLookup.TryGetValue(dynamicLayer.MapLayerId, out var layer) ? layer : null)
                .Where(static layer => layer != null)
                .Cast<IdentifyLayerDescriptor>()
                .ToArray();
        }

        IEnumerable<IdentifyLayerDescriptor> candidates = publishedLayers;
        if (mode is IdentifyLayerMode.Visible or IdentifyLayerMode.Top)
        {
            candidates = candidates.Where(static layer => layer.Resource.Display?.DefaultVisibility ?? true);
            if (scaleDenominator > 0)
            {
                candidates = candidates.Where(layer => IsLayerVisibleAtScale(layer, scaleDenominator));
            }
        }

        if (ids is { Count: > 0 })
        {
            candidates = excludeIds
                ? candidates.Where(layer => !ids.Contains(layer.PublicLayerId))
                : candidates.Where(layer => ids.Contains(layer.PublicLayerId));
        }

        return candidates.ToArray();
    }

    private static string ResolveIdentifyTelemetryLayerTag(string? layersParam, bool hasDynamicLayers)
    {
        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            return layersParam.Trim();
        }

        return hasDynamicLayers ? "dynamic:*" : "*";
    }

    private static bool IsIdentifyLayerModeToken(string token)
    {
        return string.Equals(token, "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "visible", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "top", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps an identify <c>layers</c> mode/visibility token to an <see cref="IdentifyLayerMode"/> and
    /// whether the accompanying ids should be excluded rather than selected.
    /// Accepts the identify mode tokens (<c>top</c>/<c>visible</c>/<c>all</c>) and the GeoServices
    /// visibility prefixes (<c>show</c>/<c>hide</c>/<c>include</c>/<c>exclude</c>). <c>show</c> and
    /// <c>include</c> select the listed ids; <c>hide</c> and <c>exclude</c> exclude them.
    /// </summary>
    private static (IdentifyLayerMode Mode, bool ExcludeIds) ResolveIdentifyLayerMode(string? modeToken)
    {
        return modeToken?.ToLowerInvariant() switch
        {
            "all" => (IdentifyLayerMode.All, false),
            "visible" => (IdentifyLayerMode.Visible, false),
            "top" => (IdentifyLayerMode.Top, false),
            "hide" => (IdentifyLayerMode.All, true),
            "exclude" => (IdentifyLayerMode.All, true),
            _ => (IdentifyLayerMode.All, false),
        };
    }

    private static bool HasNonIntegerIdentifyLayerToken(string? layersParam)
    {
        if (string.IsNullOrWhiteSpace(layersParam))
        {
            return false;
        }

        var spec = layersParam.Trim();
        string? idsPart = null;
        var colonIndex = spec.IndexOf(':');
        if (colonIndex >= 0)
        {
            idsPart = spec[(colonIndex + 1)..];
        }
        else if (!IsIdentifyLayerModeToken(spec))
        {
            idsPart = spec;
        }

        if (string.IsNullOrWhiteSpace(idsPart))
        {
            return false;
        }

        foreach (var token in idsPart.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildIdentifySpatialFilter(
        IdentifyGeometryInput geometry,
        string geometryType,
        double tolerance,
        int srid,
        IGeometryConverter geometryConverter,
        out SpatialFilter spatialFilter,
        out string? error)
    {
        spatialFilter = default;
        error = null;

        var kind = ResolveIdentifyGeometryKind(geometryType, geometry);

        try
        {
            switch (kind)
            {
                case IdentifyGeometryKind.Point:
                    {
                        if (!geometry.X.HasValue || !geometry.Y.HasValue)
                        {
                            error = "Point geometry must include x and y values.";
                            return false;
                        }

                        var x = geometry.X.Value;
                        var y = geometry.Y.Value;

                        var wkb = tolerance > 0
                            ? CreatePointBufferWkb(x, y, tolerance)
                            : CreatePointGeometryWkb(geometry, geometryConverter, x, y);

                        if (wkb is null)
                        {
                            error = "Point geometry is invalid.";
                            return false;
                        }

                        spatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid);
                        return true;
                    }
                case IdentifyGeometryKind.Envelope:
                    {
                        if (!geometry.Xmin.HasValue || !geometry.Ymin.HasValue || !geometry.Xmax.HasValue || !geometry.Ymax.HasValue)
                        {
                            error = "Envelope geometry must include xmin, ymin, xmax, and ymax.";
                            return false;
                        }

                        var wkb = SpatialFilterHelpers.CreateEnvelopeWkb(
                            geometry.Xmin.Value,
                            geometry.Ymin.Value,
                            geometry.Xmax.Value,
                            geometry.Ymax.Value);

                        spatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid);
                        return true;
                    }
                case IdentifyGeometryKind.MultiPoint:
                case IdentifyGeometryKind.Polyline:
                case IdentifyGeometryKind.Polygon:
                    {
                        if (!geometry.IsJson)
                        {
                            error = "Geometry must be provided as GeoServices JSON for this geometry type.";
                            return false;
                        }

                        var wkb = geometryConverter.ConvertGeoServicesJsonToWkb(geometry.RawValue);
                        if (wkb is null)
                        {
                            error = "Geometry parameter is invalid.";
                            return false;
                        }

                        spatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid);
                        return true;
                    }
                default:
                    error = "Unsupported geometry type.";
                    return false;
            }
        }
        catch (ArgumentException)
        {
            error = InvalidIdentifyGeometryMessage;
            return false;
        }
    }

    private static byte[] CreatePointBufferWkb(double x, double y, double tolerance)
    {
        return SpatialFilterHelpers.CreateEnvelopeWkb(
            x - tolerance,
            y - tolerance,
            x + tolerance,
            y + tolerance);
    }

    private static byte[]? CreatePointGeometryWkb(
        IdentifyGeometryInput geometry,
        IGeometryConverter geometryConverter,
        double x,
        double y)
    {
        if (!geometry.IsJson)
        {
            return CreatePointWkb(x, y);
        }

        return geometryConverter.ConvertGeoServicesJsonToWkb(geometry.RawValue);
    }

    private static byte[] CreatePointWkb(double x, double y)
    {
        var wkb = new byte[21];
        var offset = 0;

        wkb[offset++] = 1;
        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 1);
        offset += 4;

        BitConverter.TryWriteBytes(wkb.AsSpan(offset), x);
        offset += 8;

        BitConverter.TryWriteBytes(wkb.AsSpan(offset), y);
        return wkb;
    }

    private static IdentifyGeometryKind ResolveIdentifyGeometryKind(string geometryType, IdentifyGeometryInput geometry)
    {
        var normalized = geometryType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "esrigeometrypoint" => IdentifyGeometryKind.Point,
            "esrigeometrymultipoint" => IdentifyGeometryKind.MultiPoint,
            "esrigeometrypolyline" => IdentifyGeometryKind.Polyline,
            "esrigeometrypolygon" => IdentifyGeometryKind.Polygon,
            "esrigeometryenvelope" => IdentifyGeometryKind.Envelope,
            _ => InferIdentifyGeometryKind(geometry)
        };
    }

    private static IdentifyGeometryKind InferIdentifyGeometryKind(IdentifyGeometryInput geometry)
    {
        if (geometry.X.HasValue && geometry.Y.HasValue)
        {
            return IdentifyGeometryKind.Point;
        }

        if (geometry.HasPoints)
        {
            return IdentifyGeometryKind.MultiPoint;
        }

        if (geometry.HasPaths)
        {
            return IdentifyGeometryKind.Polyline;
        }

        if (geometry.HasRings)
        {
            return IdentifyGeometryKind.Polygon;
        }

        if (geometry.Xmin.HasValue && geometry.Ymin.HasValue && geometry.Xmax.HasValue && geometry.Ymax.HasValue)
        {
            return IdentifyGeometryKind.Envelope;
        }

        return IdentifyGeometryKind.Point;
    }

    private static bool TryGetIdentifyPoint(IdentifyGeometryInput geometry, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (geometry.X.HasValue && geometry.Y.HasValue)
        {
            x = geometry.X.Value;
            y = geometry.Y.Value;
            return true;
        }

        return false;
    }

    private static string? GetDisplayFieldValue(Feature feature, string displayField)
    {
        if (feature.Attributes.TryGetValue(displayField, out var value) && value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        return feature.Id.ToString(CultureInfo.InvariantCulture);
    }

}
