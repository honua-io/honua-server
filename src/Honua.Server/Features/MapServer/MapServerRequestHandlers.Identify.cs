// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.MapServer.Models;
using Honua.Server.Features.MapServer.Rendering;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int DefaultTolerance = 3;
    private const int MaxIdentifyResults = 100;

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

    private sealed record IdentifyLayerSelection(IdentifyLayerMode Mode, LayerDefinition[] Layers);

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
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var geometryValue = GetValue(values, "geometry");
            if (!TryParseGeometryType(GetValue(values, "geometryType"), out var geometryTypeValue, out var geometryTypeError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, geometryTypeError ?? "Invalid geometryType parameter.");
            }

            var srValue = GetValue(values, "sr");
            var layersParam = GetValue(values, "layers");
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

            if (!TryParseBbox(mapExtentValue, out var mapExtent))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid mapExtent parameter. Expected format: xmin,ymin,xmax,ymax");
            }

            if (!TryParseImageDisplay(imageDisplayValue, out var imageWidth, out var imageHeight, out var imageDpi, out var imageDisplayError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, imageDisplayError ?? "Invalid imageDisplay parameter.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }

                return StandardErrorHelpers.CreateNotFound(context, errorMessage);
            }

            var service = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            if (!TryParseLayerDefs(layerDefsValue, queryValidator, out var layerDefs, out var layerDefsError))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    layerDefsError ?? "Invalid layerDefs parameter.");
            }

            if (!TryResolveIdentifySrid(srValue, geometry, service.SpatialReference.Srid, out var geometrySrid, out var srError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, srError ?? "Invalid spatial reference.");
            }

            if (TryGetIdentifyPoint(geometry, out var pointX, out var pointY))
            {
                MapServerLog.IdentifyRequested(logger, serviceId,
                    pointX.ToString(CultureInfo.InvariantCulture),
                    pointY.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                MapServerLog.IdentifyRequested(logger, serviceId, "n/a", "n/a");
            }

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.MapServerIdentify, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "identify");

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var geometryConverter = context.RequestServices.GetRequiredService<IGeometryConverter>();

            var scaleDenominator = CoordinateTransformer.CalculateScaleDenominator(mapExtent, imageWidth, imageDpi, geometrySrid);
            var identifySelection = ResolveIdentifyLayers(service, layersParam, context, scaleDenominator);

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
                ? identifySelection.Layers.Reverse().ToArray()
                : identifySelection.Layers;

            foreach (var layer in layersToSearch)
            {
                if (!layer.HasGeometry)
                {
                    continue;
                }

                layerDefs.TryGetValue(layer.Id, out var layerDef);

                var featureQuery = new FeatureQuery
                {
                    SpatialFilter = spatialFilter,
                    SpatialReferenceSrid = service.SpatialReference.Srid,
                    OutputSrid = geometrySrid,
                    Limit = MaxIdentifyResults,
                    Where = layerDef
                };

                var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);

                if (queryResult.Items.Length == 0)
                {
                    continue;
                }

                foreach (var feature in queryResult.Items)
                {
                    var attributes = new Dictionary<string, object?>();
                    foreach (var kvp in feature.Attributes)
                    {
                        attributes[kvp.Key] = kvp.Value;
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

                    var displayValue = GetDisplayFieldValue(feature, layer);

                    var identifyResult = new IdentifyResult
                    {
                        LayerId = layer.Id,
                        LayerName = layer.Name,
                        Value = displayValue,
                        Attributes = attributes,
                        GeometryType = MapGeometryTypeToEsri(layer.GeometryType),
                        Geometry = geometryResult
                    };

                    results.Add(identifyResult);
                }

                if (identifySelection.Mode == IdentifyLayerMode.Top && results.Count > 0)
                {
                    break;
                }
            }

            MapServerLog.IdentifyCompleted(logger, serviceId, results.Count);
            HonuaTelemetry.SetSuccess(activity, results.Count);

            var response = new IdentifyResponse { Results = [.. results] };
            return Results.Json(response, MapServerJsonContext.Default.IdentifyResponse, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            MapServerLog.IdentifyFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            MapServerLog.IdentifyFailed(logger, serviceId, ex.Message, ex);
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

        var parts = imageDisplay.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
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
            var parsed = TryParseSrid(srValue);
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

    private static bool TryParseIdentifyGeometry(
        string geometryValue,
        string geometryType,
        out IdentifyGeometryInput geometry,
        out string? error)
    {
        geometry = default!;
        error = null;

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

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
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

    private static IdentifyLayerSelection ResolveIdentifyLayers(
        ServiceDefinition service,
        string? layersParam,
        HttpContext context,
        double scaleDenominator)
    {
        var accessibleLayers = service.Layers
            .Where(l => AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();

        IdentifyLayerMode mode = IdentifyLayerMode.Top;
        HashSet<int>? ids = null;

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

            mode = modeToken?.ToLowerInvariant() switch
            {
                "all" => IdentifyLayerMode.All,
                "visible" => IdentifyLayerMode.Visible,
                "top" => IdentifyLayerMode.Top,
                _ => IdentifyLayerMode.All
            };

            if (!string.IsNullOrWhiteSpace(idPart))
            {
                ids = ParseLayerIds(idPart);
            }
        }

        IEnumerable<LayerDefinition> candidates = accessibleLayers;

        if (mode is IdentifyLayerMode.Visible or IdentifyLayerMode.Top)
        {
            candidates = candidates.Where(l => l.DefaultVisibility);
            if (scaleDenominator > 0)
            {
                candidates = candidates.Where(l => IsLayerVisibleAtScale(l, scaleDenominator));
            }
        }

        if (ids is { Count: > 0 })
        {
            candidates = candidates.Where(l => ids.Contains(l.Id));
        }

        return new IdentifyLayerSelection(mode, candidates.ToArray());
    }

    private static bool IsIdentifyLayerModeToken(string token)
    {
        return string.Equals(token, "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "visible", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(token, "top", StringComparison.OrdinalIgnoreCase);
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

                    var wkb = CreateEnvelopeWkb(
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
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] CreatePointBufferWkb(double x, double y, double tolerance)
    {
        return CreateEnvelopeWkb(
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

    private static string? GetDisplayFieldValue(Feature feature, LayerDefinition layer)
    {
        // Try first string attribute as display value
        foreach (var kvp in feature.Attributes)
        {
            if (kvp.Value is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return feature.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static string? MapGeometryTypeToEsri(GeometryType geometryType)
    {
        return geometryType switch
        {
            GeometryType.Point => "esriGeometryPoint",
            GeometryType.MultiPoint => "esriGeometryMultipoint",
            GeometryType.LineString or
            GeometryType.MultiLineString => "esriGeometryPolyline",
            GeometryType.Polygon or
            GeometryType.MultiPolygon => "esriGeometryPolygon",
            _ => null
        };
    }
}
