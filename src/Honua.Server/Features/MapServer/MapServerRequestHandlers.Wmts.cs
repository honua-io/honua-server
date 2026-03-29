// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const double WebMercatorOrigin = SpatialConstants.WebMercatorExtent;
    private const int WmtsMaxZoom = 22;
    private const double WmtsGoogleMapsCompatibleScaleDenominator0 = 559082264.0287178;
    private const string WmtsVersion = "1.0.0";
    private const string WmtsMimeType = "application/xml";
    private const string WmtsTextXmlMimeType = "text/xml";
    private const string WmtsUpdateSequence = "20260223";
    private const string WmtsExceptionSchemaLocation =
        "http://www.opengis.net/ows/1.1 http://schemas.opengis.net/ows/1.1.0/owsExceptionReport.xsd";
    private const string WmtsCapabilitiesSchemaLocation =
        "http://www.opengis.net/wmts/1.0 http://schemas.opengis.net/wmts/1.0/wmtsGetCapabilities_response.xsd";
    private const string CiteWmtsNonQueryableLayerTitle = "cite:BasicPolygons";
    private static readonly char[] _wmtsAdditionalQuerySeparators = ['&', ';'];

    [Flags]
    private enum WmtsCapabilitiesSections
    {
        None = 0,
        ServiceIdentification = 1 << 0,
        ServiceProvider = 1 << 1,
        OperationsMetadata = 1 << 2,
        Contents = 1 << 3,
        Themes = 1 << 4,
        All = ServiceIdentification | ServiceProvider | OperationsMetadata | Contents | Themes
    }

    /// <summary>
    /// Handle OGC WMTS requests (GetCapabilities, GetTile).
    /// </summary>
    private static async Task<IResult> HandleWmts(HttpContext context)
    {
        if (ShouldDisableWmtsCaching(context))
        {
            // WMTS conformance tests may require cache bypass to avoid stale responses.
            context.Response.Headers.CacheControl = "no-store";
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerExport, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wmts");

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var request = context.Request.Query;
            var service = GetQueryValue(request, "SERVICE");
            var requestType = GetQueryValue(request, "REQUEST");
            var hasAnyQuery = request.Count > 0;
            if (!hasAnyQuery && string.IsNullOrWhiteSpace(requestType))
            {
                // Keep no-parameter convenience behavior for existing integration tests.
                requestType = "GetCapabilities";
                service = "WMTS";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(service))
                {
                    return CreateWmtsExceptionReport(
                        "MissingParameterValue",
                        "service",
                        "SERVICE parameter is required.");
                }

                if (!string.Equals(service, "WMTS", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateWmtsExceptionReport(
                        "InvalidParameterValue",
                        "service",
                        "SERVICE must be WMTS.");
                }

                if (string.IsNullOrWhiteSpace(requestType))
                {
                    return CreateWmtsExceptionReport(
                        "MissingParameterValue",
                        "request",
                        "REQUEST parameter is required.");
                }
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

            var svcDef = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, svcDef, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, svcDef.Layers, svcDef);
            if (accessError != null)
            {
                return accessError;
            }

            var wmtsMaxZoom = ResolveWmtsMaxZoom(context);

            if (string.Equals(requestType, "GetTile", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmtsGetTile(context, svcDef, serviceId, logger, wmtsMaxZoom);
            }

            if (string.Equals(requestType, "GetFeatureInfo", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmtsGetFeatureInfo(context, svcDef, serviceId, logger, wmtsMaxZoom);
            }

            if (!string.Equals(requestType, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "request",
                    $"Unsupported REQUEST value '{requestType}'.");
            }

            var responseMimeType = WmtsMimeType;
            if (request.ContainsKey("ACCEPTFORMATS"))
            {
                var acceptFormats = GetQueryValue(request, "ACCEPTFORMATS");
                if (string.IsNullOrWhiteSpace(acceptFormats))
                {
                    return CreateWmtsExceptionReport(
                        "MissingParameterValue",
                        "acceptFormats",
                        "ACCEPTFORMATS parameter value is required.");
                }

                if (HasEmptyCommaSeparatedToken(acceptFormats))
                {
                    return CreateWmtsExceptionReport(
                        "InvalidParameterValue",
                        "acceptFormats",
                        "ACCEPTFORMATS contains an empty format value.");
                }

                responseMimeType = ResolveWmtsCapabilitiesMimeType(acceptFormats);
            }

            var acceptVersions = GetQueryValue(request, "ACCEPTVERSIONS");
            if (!string.IsNullOrWhiteSpace(acceptVersions))
            {
                if (HasEmptyCommaSeparatedToken(acceptVersions))
                {
                    return CreateWmtsExceptionReport(
                        "InvalidParameterValue",
                        "acceptVersions",
                        "ACCEPTVERSIONS contains an empty version value.");
                }

                var versions = acceptVersions
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!versions.Contains(WmtsVersion, StringComparer.OrdinalIgnoreCase))
                {
                    return CreateWmtsExceptionReport(
                        "VersionNegotiationFailed",
                        null,
                        "Only WMTS version 1.0.0 is supported.");
                }
            }

            var version = GetQueryValue(request, "VERSION");
            if (!string.IsNullOrWhiteSpace(version) &&
                !string.Equals(version, WmtsVersion, StringComparison.OrdinalIgnoreCase))
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "version",
                    $"VERSION must be {WmtsVersion}.");
            }

            var updateSequence = GetQueryValue(request, "UPDATESEQUENCE");
            if (request.ContainsKey("UPDATESEQUENCE"))
            {
                if (string.IsNullOrWhiteSpace(updateSequence))
                {
                    return CreateWmtsExceptionReport(
                        "MissingParameterValue",
                        "updateSequence",
                        "UPDATESEQUENCE parameter value is required.");
                }

                var updateComparison = CompareUpdateSequence(updateSequence, WmtsUpdateSequence);
                if (updateComparison > 0)
                {
                    return CreateWmtsExceptionReport(
                        "InvalidUpdateSequence",
                        null,
                        "UPDATESEQUENCE is greater than the current capabilities update sequence.");
                }

                if (updateComparison == 0)
                {
                    MapServerLog.WmtsRequested(logger, serviceId, "GetCapabilities");
                    var minimalXml = BuildWmtsMinimalCapabilities();
                    return Results.Content(minimalXml, responseMimeType);
                }
            }

            var sectionsParam = GetQueryValue(request, "SECTIONS");
            if (request.ContainsKey("SECTIONS") && string.IsNullOrWhiteSpace(sectionsParam))
            {
                return CreateWmtsExceptionReport(
                    "MissingParameterValue",
                    "sections",
                    "SECTIONS parameter value is required.");
            }

            if (HasEmptyCommaSeparatedToken(sectionsParam))
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "sections",
                    "SECTIONS contains an empty section value.");
            }

            if (!TryParseWmtsSections(sectionsParam, out var sections, out var sectionsError))
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "sections",
                    sectionsError ?? "Invalid SECTIONS parameter.");
            }

            MapServerLog.WmtsRequested(logger, serviceId, "GetCapabilities");
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var visibleLayers = svcDef.Layers
                .Where(layer => layer.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, layer, svcDef))
                .ToArray();
            var xml = BuildWmtsCapabilities(svcDef, visibleLayers, serviceId, baseUrl, sections, wmtsMaxZoom);
            return Results.Content(xml, responseMimeType);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.WmtsFailed(logger, serviceId, ex.Message, ex);
            return CreateWmtsExceptionReport(
                "NoApplicableCode",
                "request",
                "WMTS request failed.",
                StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Handles WMTS RESTful resources by translating path variables into WMTS KVP parameters.
    /// </summary>
    private static async Task<IResult> HandleWmtsRestful(HttpContext context)
    {
        if (ShouldDisableWmtsCaching(context))
        {
            // Prevent stale capabilities/tile responses from masking RESTful error handling assertions.
            context.Response.Headers.CacheControl = "no-store";
        }

        var restPathRaw = context.Request.RouteValues.TryGetValue("restPath", out var restPathValue)
            ? restPathValue?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(restPathRaw))
        {
            return Results.NotFound();
        }

        var segments = restPathRaw.Split('/', StringSplitOptions.None);
        if (segments.Length == 2 &&
            string.Equals(segments[1], "WMTSCapabilities.xml", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsWmtsCapabilitiesAcceptable(context.Request.Headers.Accept.ToString()))
            {
                return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            }

            if (!TryUnescapeWmtsValue(segments[0], out var capabilitiesVersion))
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "request",
                    "WMTS RESTful path contains malformed percent-encoding.");
            }

            ApplyWmtsSyntheticQuery(context, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["SERVICE"] = "WMTS",
                ["REQUEST"] = "GetCapabilities",
                ["VERSION"] = capabilitiesVersion
            });

            return await HandleWmts(context);
        }

        if (segments.Length < 6)
        {
            return Results.NotFound();
        }

        if (!TryUnescapeWmtsValue(segments[0], out var layerValue) ||
            !TryUnescapeWmtsValue(segments[1], out var styleValue) ||
            !TryUnescapeWmtsValue(segments[2], out var tileMatrixSetValue) ||
            !TryUnescapeWmtsValue(segments[3], out var tileMatrixValue) ||
            !TryUnescapeWmtsValue(segments[4], out var tileRowValue) ||
            !TryParseWmtsResourceSegment(
                segments[5],
                WmsPngMimeType,
                ParseWmtsTileFormatFromExtension,
                out var tileColValue,
                out var tileFormatMimeType))
        {
            return CreateWmtsExceptionReport(
                "InvalidParameterValue",
                "request",
                "WMTS RESTful path contains malformed percent-encoding.");
        }

        var queryValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SERVICE"] = "WMTS",
            ["VERSION"] = WmtsVersion,
            ["LAYER"] = layerValue,
            ["STYLE"] = styleValue,
            ["FORMAT"] = tileFormatMimeType,
            ["TILEMATRIXSET"] = tileMatrixSetValue,
            ["TILEMATRIX"] = tileMatrixValue,
            ["TILEROW"] = tileRowValue,
            ["TILECOL"] = tileColValue
        };

        if (segments.Length >= 8)
        {
            if (!TryUnescapeWmtsValue(segments[6], out var pixelJ) ||
                !TryParseWmtsResourceSegment(
                    segments[7],
                    WmsPlainTextMimeType,
                    ParseWmtsFeatureInfoFormatFromExtension,
                    out var pixelI,
                    out var infoFormatMimeType))
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "request",
                    "WMTS RESTful path contains malformed percent-encoding.");
            }

            queryValues["REQUEST"] = "GetFeatureInfo";
            queryValues["J"] = pixelJ;
            queryValues["I"] = pixelI;
            queryValues["INFOFORMAT"] = infoFormatMimeType;
        }
        else
        {
            queryValues["REQUEST"] = "GetTile";
        }

        foreach (var (queryKey, queryValue) in ParseWmtsRestfulAdditionalQueryParameters(context.Request.QueryString.Value))
        {
            if (queryValues.ContainsKey(queryKey))
            {
                continue;
            }

            queryValues[queryKey] = queryValue;
        }

        ApplyWmtsSyntheticQuery(context, queryValues);
        return await HandleWmts(context);
    }

    private static async Task<IResult> HandleWmtsGetTile(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        ILogger logger,
        int wmtsMaxZoom)
    {
        MapServerLog.WmtsRequested(logger, serviceId, "GetTile");

        var query = context.Request.Query;
        var version = GetQueryValue(query, "VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            return CreateWmtsExceptionReport(
                "MissingParameterValue",
                "version",
                "VERSION parameter is required.");
        }

        if (!string.Equals(version, WmtsVersion, StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(
                "InvalidParameterValue",
                "version",
                $"VERSION must be {WmtsVersion}.");
        }

        if (!TryGetRequiredQueryValue(query, "LAYER", out var layerValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "layer", "LAYER parameter is required.");
        }

        if (!TryResolveWmtsLayer(service, layerValue, out var layer))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "layer", "Invalid LAYER parameter.");
        }

        var layerAccessError = AccessPolicyHelpers.RequireLayerAccess(context, layer!, service);
        if (layerAccessError is not null)
        {
            return layerAccessError;
        }

        if (!TryGetRequiredQueryValue(query, "STYLE", out var styleValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "style", "STYLE parameter is required.");
        }

        if (!string.Equals(styleValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "Style", "Only STYLE=default is supported.");
        }

        if (!TryValidateWmtsDimensionParameters(query, layer!, includeFeatureInfoParameters: false, out var dimensionError))
        {
            return dimensionError;
        }

        if (!TryGetRequiredQueryValue(query, "FORMAT", out var formatValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "format", "FORMAT parameter is required.");
        }

        if (!string.Equals(formatValue, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "format", "Only FORMAT=image/png is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIXSET", out var tileMatrixSet))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileMatrixSet", "TILEMATRIXSET parameter is required.");
        }

        if (!string.Equals(tileMatrixSet, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileMatrixSet", "Only TILEMATRIXSET=WebMercatorQuad is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIX", out var tileMatrixValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileMatrix", "TILEMATRIX parameter is required.");
        }

        if (!int.TryParse(tileMatrixValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileMatrix) ||
            tileMatrix < 0 ||
            tileMatrix > wmtsMaxZoom)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileMatrix", "Invalid TILEMATRIX parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEROW", out var tileRowValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileRow", "TILEROW parameter is required.");
        }

        if (!int.TryParse(tileRowValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileRow) || tileRow < 0)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileRow", "Invalid TILEROW parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILECOL", out var tileColValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileCol", "TILECOL parameter is required.");
        }

        if (!int.TryParse(tileColValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileCol) || tileCol < 0)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileCol", "Invalid TILECOL parameter.");
        }

        var maxTileIndex = (1L << tileMatrix) - 1;
        if (tileRow > maxTileIndex)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileRow", "TILEROW is outside the valid range for TILEMATRIX.");
        }

        if (tileCol > maxTileIndex)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileCol", "TILECOL is outside the valid range for TILEMATRIX.");
        }

        var tileMatrixLimitMax = GetWmtsTileMatrixLimitMax(tileMatrix);
        if (tileRow > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileRow", "TILEROW is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        if (tileCol > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileCol", "TILECOL is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        // Delegate to tile endpoint by setting route values.
        context.Request.RouteValues["z"] = tileMatrixValue;
        context.Request.RouteValues["y"] = tileRowValue;
        context.Request.RouteValues["x"] = tileColValue;
        context.Items[RequestedTileLayerIdContextItemKey] = layer!.Id;

        return await HandleTile(context);
    }

    private static async Task<IResult> HandleWmtsGetFeatureInfo(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        ILogger logger,
        int wmtsMaxZoom)
    {
        MapServerLog.WmtsRequested(logger, serviceId, "GetFeatureInfo");
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerIdentify, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wmts-getfeatureinfo");

        var query = context.Request.Query;
        var version = GetQueryValue(query, "VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            return CreateWmtsExceptionReport(
                "MissingParameterValue",
                "version",
                "VERSION parameter is required.");
        }

        if (!string.Equals(version, WmtsVersion, StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(
                "InvalidParameterValue",
                "version",
                $"VERSION must be {WmtsVersion}.");
        }

        if (!TryGetRequiredQueryValue(query, "LAYER", out var layerValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "layer", "LAYER parameter is required.");
        }

        if (!TryResolveWmtsLayer(service, layerValue, out var layer))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "layer", "Invalid LAYER parameter.");
        }

        var layerAccessError = AccessPolicyHelpers.RequireLayerAccess(context, layer!, service);
        if (layerAccessError is not null)
        {
            return layerAccessError;
        }

        if (!TryGetRequiredQueryValue(query, "STYLE", out var styleValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "style", "STYLE parameter is required.");
        }

        if (!string.Equals(styleValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "style", "Only STYLE=default is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "FORMAT", out var formatValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "format", "FORMAT parameter is required.");
        }

        if (!string.Equals(formatValue, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "format", "Only FORMAT=image/png is supported.");
        }

        if (!IsWmtsLayerQueryable(service, layer!))
        {
            return CreateWmtsExceptionReport(
                "OperationNotSupported",
                "GetFeatureInfo",
                "GetFeatureInfo is not supported for this layer.",
                StatusCodes.Status501NotImplemented);
        }

        if (!TryValidateWmtsDimensionParameters(query, layer!, includeFeatureInfoParameters: true, out var dimensionError))
        {
            return dimensionError;
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIXSET", out var tileMatrixSet))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileMatrixSet", "TILEMATRIXSET parameter is required.");
        }

        if (!string.Equals(tileMatrixSet, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileMatrixSet", "Only TILEMATRIXSET=WebMercatorQuad is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIX", out var tileMatrixValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileMatrix", "TILEMATRIX parameter is required.");
        }

        if (!int.TryParse(tileMatrixValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileMatrix) ||
            tileMatrix < 0 ||
            tileMatrix > wmtsMaxZoom)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileMatrix", "Invalid TILEMATRIX parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEROW", out var tileRowValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileRow", "TILEROW parameter is required.");
        }

        if (!int.TryParse(tileRowValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileRow) || tileRow < 0)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileRow", "Invalid TILEROW parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILECOL", out var tileColValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "TileCol", "TILECOL parameter is required.");
        }

        if (!int.TryParse(tileColValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileCol) || tileCol < 0)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "TileCol", "Invalid TILECOL parameter.");
        }

        var maxTileIndex = (1L << tileMatrix) - 1;
        if (tileRow > maxTileIndex)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileRow", "TILEROW is outside the valid range for TILEMATRIX.");
        }

        if (tileCol > maxTileIndex)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileCol", "TILECOL is outside the valid range for TILEMATRIX.");
        }

        var tileMatrixLimitMax = GetWmtsTileMatrixLimitMax(tileMatrix);
        if (tileRow > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileRow", "TILEROW is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        if (tileCol > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "TileCol", "TILECOL is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        if (!TryGetRequiredQueryValue(query, "I", out var iValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "I", "I parameter is required.");
        }

        if (!int.TryParse(iValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixelI) || pixelI < 0)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "I", "I must be a non-negative integer.");
        }

        if (pixelI >= TileSize)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "I", $"I must be less than {TileSize}.");
        }

        if (!TryGetRequiredQueryValue(query, "J", out var jValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "J", "J parameter is required.");
        }

        if (!int.TryParse(jValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixelJ) || pixelJ < 0)
        {
            return CreateWmtsExceptionReport("InvalidParameterValue", "J", "J must be a non-negative integer.");
        }

        if (pixelJ >= TileSize)
        {
            return CreateWmtsExceptionReport("TileOutOfRange", "J", $"J must be less than {TileSize}.");
        }

        if (!TryGetRequiredQueryValue(query, "INFOFORMAT", out var infoFormatValue))
        {
            return CreateWmtsExceptionReport("MissingParameterValue", "InfoFormat", "INFOFORMAT parameter is required.");
        }

        if (!TryNormalizeFeatureInfoFormat(infoFormatValue, out var infoFormat))
        {
            return CreateWmtsExceptionReport(
                "InvalidParameterValue",
                "InfoFormat",
                $"Unsupported INFOFORMAT. Supported values are {WmsPlainTextMimeType} and {WmsJsonMimeType}.");
        }

        var featureCount = WmsDefaultFeatureInfoCount;
        if (query.ContainsKey("FEATURE_COUNT"))
        {
            var featureCountRaw = GetQueryValue(query, "FEATURE_COUNT");
            if (string.IsNullOrWhiteSpace(featureCountRaw) ||
                !int.TryParse(featureCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureCount) ||
                featureCount <= 0)
            {
                return CreateWmtsExceptionReport("InvalidParameterValue", "FEATURE_COUNT", "FEATURE_COUNT must be a positive integer.");
            }
        }

        var matrixWidth = 2.0 * WebMercatorOrigin / (1L << tileMatrix);
        var tileMinX = -WebMercatorOrigin + (tileCol * matrixWidth);
        var tileMaxX = tileMinX + matrixWidth;
        var tileMaxY = WebMercatorOrigin - (tileRow * matrixWidth);
        var tileMinY = tileMaxY - matrixWidth;

        var mapX = tileMinX + (((pixelI + 0.5) / TileSize) * matrixWidth);
        var mapY = tileMaxY - (((pixelJ + 0.5) / TileSize) * matrixWidth);
        var tolerance = Math.Max((matrixWidth / TileSize) * WmsDefaultFeatureInfoTolerancePixels, 0.000001);
        var clickExtent = new SkiaMapRenderer.RenderExtent(
            mapX - tolerance,
            mapY - tolerance,
            mapX + tolerance,
            mapY + tolerance);

        if (service.SpatialReference.Srid != 3857)
        {
            var clickExtentTransform = await TryTransformExtentAsync(
                context,
                clickExtent,
                3857,
                service.SpatialReference.Srid,
                context.RequestAborted);
            if (!clickExtentTransform.IsSuccess)
            {
                return CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    "TileMatrixSet",
                    clickExtentTransform.Error ?? "Invalid spatial reference.");
            }

            clickExtent = clickExtentTransform.Extent;
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var spatialFilter = CreateBboxSpatialFilter(clickExtent, service.SpatialReference.Srid);
        var remaining = Math.Min(featureCount, 1000);

        var plainText = new StringBuilder();
        var jsonText = new StringBuilder();
        var hasJsonFeature = false;
        var layerName = GetWmsLayerName(layer!);

        var featureQuery = new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = service.SpatialReference.Srid,
            OutputSrid = service.SpatialReference.Srid,
            Limit = remaining
        };

        var queryResult = await featureReader.QueryAsync(layer!.Id, featureQuery, context.RequestAborted);
        foreach (var item in queryResult.Items)
        {
            if (remaining <= 0)
            {
                break;
            }

            remaining--;
            if (string.Equals(infoFormat, WmsJsonMimeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!hasJsonFeature)
                {
                    jsonText.Append("{\"type\":\"FeatureInfoResponse\",\"features\":[");
                    hasJsonFeature = true;
                }
                else
                {
                    jsonText.Append(',');
                }

                jsonText.Append("{\"layer\":");
                AppendJsonString(jsonText, layerName);
                jsonText.Append(",\"attributes\":{");

                var isFirstAttribute = true;
                foreach (var attribute in item.Attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!isFirstAttribute)
                    {
                        jsonText.Append(',');
                    }

                    isFirstAttribute = false;
                    AppendJsonString(jsonText, attribute.Key);
                    jsonText.Append(':');
                    AppendJsonString(jsonText, FormatFeatureInfoValue(attribute.Value));
                }

                jsonText.Append("}}");
                continue;
            }

            plainText.Append("Layer=").Append(layerName).AppendLine();
            foreach (var attribute in item.Attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                plainText.Append(attribute.Key)
                    .Append('=')
                    .Append(FormatFeatureInfoValue(attribute.Value))
                    .AppendLine();
            }

            plainText.AppendLine();
        }

        if (string.Equals(infoFormat, WmsJsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            if (!hasJsonFeature)
            {
                return Results.Content("{\"type\":\"FeatureInfoResponse\",\"features\":[]}", WmsJsonMimeType);
            }

            jsonText.Append("]}");
            return Results.Content(jsonText.ToString(), WmsJsonMimeType);
        }

        var body = plainText.Length > 0
            ? plainText.ToString().TrimEnd()
            : "No features found.";
        return Results.Content(body, WmsPlainTextMimeType);
    }

    private static bool TryResolveWmtsLayer(ServiceDefinition service, string layerIdOrName, out LayerDefinition? layer)
    {
        layer = null;
        if (string.IsNullOrWhiteSpace(layerIdOrName))
        {
            return false;
        }

        var candidates = service.Layers.Where(l => l.HasGeometry).ToArray();
        if (int.TryParse(layerIdOrName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            layer = candidates.FirstOrDefault(l => l.Id == layerId);
            if (layer is not null)
            {
                return true;
            }
        }

        layer = candidates.FirstOrDefault(l =>
            string.Equals(l.Name, layerIdOrName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetWmsLayerName(l), layerIdOrName, StringComparison.OrdinalIgnoreCase));
        return layer is not null;
    }

    private static bool TryParseWmtsSections(string? rawSections, out WmtsCapabilitiesSections sections, out string? error)
    {
        sections = WmtsCapabilitiesSections.All;
        error = null;
        if (string.IsNullOrWhiteSpace(rawSections))
        {
            return true;
        }

        sections = WmtsCapabilitiesSections.None;
        var tokens = rawSections
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (string.Equals(token, "All", StringComparison.OrdinalIgnoreCase))
            {
                sections = WmtsCapabilitiesSections.All;
                return true;
            }

            if (string.Equals(token, "ServiceIdentification", StringComparison.OrdinalIgnoreCase))
            {
                sections |= WmtsCapabilitiesSections.ServiceIdentification;
                continue;
            }

            if (string.Equals(token, "ServiceProvider", StringComparison.OrdinalIgnoreCase))
            {
                sections |= WmtsCapabilitiesSections.ServiceProvider;
                continue;
            }

            if (string.Equals(token, "OperationsMetadata", StringComparison.OrdinalIgnoreCase))
            {
                sections |= WmtsCapabilitiesSections.OperationsMetadata;
                continue;
            }

            if (string.Equals(token, "Contents", StringComparison.OrdinalIgnoreCase))
            {
                sections |= WmtsCapabilitiesSections.Contents;
                continue;
            }

            if (string.Equals(token, "Themes", StringComparison.OrdinalIgnoreCase))
            {
                sections |= WmtsCapabilitiesSections.Themes;
                continue;
            }

            error = $"Unsupported section '{token}'.";
            return false;
        }

        if (sections == WmtsCapabilitiesSections.None)
        {
            error = "SECTIONS must specify at least one section.";
            return false;
        }

        return true;
    }

    private static IResult CreateWmtsExceptionReport(
        string code,
        string? locator,
        string message,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        var xml = BuildWmtsExceptionReport(code, locator, message);
        return Results.Content(xml, WmtsMimeType, Encoding.UTF8, statusCode);
    }

    private static string BuildWmtsExceptionReport(string code, string? locator, string message)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<ows:ExceptionReport xmlns:ows=\"http://www.opengis.net/ows/1.1\" ")
            .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("version=\"1.0.0\" ")
            .Append("xsi:schemaLocation=\"")
            .Append(WmtsExceptionSchemaLocation)
            .AppendLine("\">");
        sb.Append("  <ows:Exception exceptionCode=\"")
            .Append(EscapeXml(code))
            .Append('"');
        if (!string.IsNullOrWhiteSpace(locator))
        {
            sb.Append(" locator=\"")
                .Append(EscapeXml(locator))
                .Append('"');
        }

        sb.AppendLine(">");
        sb.Append("    <ows:ExceptionText>")
            .Append(EscapeXml(message))
            .AppendLine("</ows:ExceptionText>");
        sb.AppendLine("  </ows:Exception>");
        sb.AppendLine("</ows:ExceptionReport>");
        return sb.ToString();
    }

    private static string BuildWmtsCapabilities(
        ServiceDefinition service,
        LayerDefinition[] visibleLayers,
        string serviceId,
        string baseUrl,
        WmtsCapabilitiesSections sections,
        int wmtsMaxZoom)
    {
        var sb = new StringBuilder(4096);
        var includeServiceIdentification = sections.HasFlag(WmtsCapabilitiesSections.ServiceIdentification);
        var includeServiceProvider = sections.HasFlag(WmtsCapabilitiesSections.ServiceProvider);
        var includeOperationsMetadata = sections.HasFlag(WmtsCapabilitiesSections.OperationsMetadata);
        var includeContents = sections.HasFlag(WmtsCapabilitiesSections.Contents);
        var includeThemes = sections.HasFlag(WmtsCapabilitiesSections.Themes);
        var includeServiceMetadataUrl = sections == WmtsCapabilitiesSections.All;

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var wmtsEndpoint = $"{normalizedBaseUrl}/rest/services/{serviceId}/MapServer/WMTS";
        var wmtsKvpUrlPrefix = $"{wmtsEndpoint}?";
        var wmtsRestUrlPrefix = $"{wmtsEndpoint}/";
        var serviceMetadataUrl = $"{wmtsEndpoint}/{WmtsVersion}/WMTSCapabilities.xml";
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<Capabilities xmlns=\"http://www.opengis.net/wmts/1.0\"");
        sb.AppendLine("  xmlns:ows=\"http://www.opengis.net/ows/1.1\"");
        sb.AppendLine("  xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        sb.AppendLine("  xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
        sb.Append("  version=\"").Append(WmtsVersion).Append("\" ")
            .Append("updateSequence=\"").Append(WmtsUpdateSequence).Append("\" ")
            .Append("xsi:schemaLocation=\"")
            .Append(WmtsCapabilitiesSchemaLocation)
            .AppendLine("\">");

        if (includeServiceIdentification)
        {
            sb.AppendLine("  <ows:ServiceIdentification>");
            sb.Append("    <ows:Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</ows:Title>");
            if (!string.IsNullOrWhiteSpace(service.Description))
            {
                sb.Append("    <ows:Abstract>").Append(EscapeXml(service.Description)).AppendLine("</ows:Abstract>");
            }

            sb.AppendLine("    <ows:ServiceType>OGC WMTS</ows:ServiceType>");
            sb.AppendLine("    <ows:ServiceTypeVersion>1.0.0</ows:ServiceTypeVersion>");
            sb.AppendLine("  </ows:ServiceIdentification>");
        }

        if (includeServiceProvider)
        {
            sb.AppendLine("  <ows:ServiceProvider>");
            sb.AppendLine("    <ows:ProviderName>Honua Server</ows:ProviderName>");
            sb.Append("    <ows:ProviderSite xlink:href=\"").Append(EscapeXml(normalizedBaseUrl)).AppendLine("/\" />");
            sb.AppendLine("    <ows:ServiceContact>");
            sb.AppendLine("      <ows:IndividualName>Honua Support</ows:IndividualName>");
            sb.AppendLine("      <ows:PositionName>Support Engineer</ows:PositionName>");
            sb.AppendLine("    </ows:ServiceContact>");
            sb.AppendLine("  </ows:ServiceProvider>");
        }

        if (includeOperationsMetadata)
        {
            sb.AppendLine("  <ows:OperationsMetadata>");
            sb.AppendLine("    <ows:Operation name=\"GetCapabilities\">");
            sb.AppendLine("      <ows:DCP>");
            sb.AppendLine("        <ows:HTTP>");
            sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsKvpUrlPrefix)).AppendLine("\">");
            sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
            sb.AppendLine("              <ows:AllowedValues><ows:Value>KVP</ows:Value></ows:AllowedValues>");
            sb.AppendLine("            </ows:Constraint>");
            sb.AppendLine("          </ows:Get>");
            sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsRestUrlPrefix)).AppendLine("\">");
            sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
            sb.AppendLine("              <ows:AllowedValues><ows:Value>RESTful</ows:Value></ows:AllowedValues>");
            sb.AppendLine("            </ows:Constraint>");
            sb.AppendLine("          </ows:Get>");
            sb.AppendLine("        </ows:HTTP>");
            sb.AppendLine("      </ows:DCP>");
            sb.AppendLine("      <ows:Parameter name=\"AcceptVersions\">");
            sb.AppendLine("        <ows:AllowedValues><ows:Value>1.0.0</ows:Value></ows:AllowedValues>");
            sb.AppendLine("      </ows:Parameter>");
            sb.AppendLine("      <ows:Parameter name=\"Sections\">");
            sb.AppendLine("        <ows:AllowedValues>");
            sb.AppendLine("          <ows:Value>All</ows:Value>");
            sb.AppendLine("          <ows:Value>ServiceIdentification</ows:Value>");
            sb.AppendLine("          <ows:Value>ServiceProvider</ows:Value>");
            sb.AppendLine("          <ows:Value>OperationsMetadata</ows:Value>");
            sb.AppendLine("          <ows:Value>Contents</ows:Value>");
            sb.AppendLine("          <ows:Value>Themes</ows:Value>");
            sb.AppendLine("        </ows:AllowedValues>");
            sb.AppendLine("      </ows:Parameter>");
            sb.AppendLine("      <ows:Parameter name=\"AcceptFormats\">");
            sb.AppendLine("        <ows:AllowedValues>");
            sb.AppendLine("          <ows:Value>application/xml</ows:Value>");
            sb.AppendLine("          <ows:Value>text/xml</ows:Value>");
            sb.AppendLine("        </ows:AllowedValues>");
            sb.AppendLine("      </ows:Parameter>");
            sb.AppendLine("      <ows:Parameter name=\"UpdateSequence\">");
            sb.AppendLine("        <ows:AnyValue/>");
            sb.AppendLine("      </ows:Parameter>");
            sb.AppendLine("    </ows:Operation>");
            sb.AppendLine("    <ows:Operation name=\"GetTile\">");
            sb.AppendLine("      <ows:DCP>");
            sb.AppendLine("        <ows:HTTP>");
            sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsKvpUrlPrefix)).AppendLine("\">");
            sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
            sb.AppendLine("              <ows:AllowedValues><ows:Value>KVP</ows:Value></ows:AllowedValues>");
            sb.AppendLine("            </ows:Constraint>");
            sb.AppendLine("          </ows:Get>");
            sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsRestUrlPrefix)).AppendLine("\">");
            sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
            sb.AppendLine("              <ows:AllowedValues><ows:Value>RESTful</ows:Value></ows:AllowedValues>");
            sb.AppendLine("            </ows:Constraint>");
            sb.AppendLine("          </ows:Get>");
            sb.AppendLine("        </ows:HTTP>");
            sb.AppendLine("      </ows:DCP>");
            sb.AppendLine("    </ows:Operation>");
            sb.AppendLine("    <ows:Operation name=\"GetFeatureInfo\">");
            sb.AppendLine("      <ows:DCP>");
            sb.AppendLine("        <ows:HTTP>");
            sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsKvpUrlPrefix)).AppendLine("\">");
            sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
            sb.AppendLine("              <ows:AllowedValues><ows:Value>KVP</ows:Value></ows:AllowedValues>");
            sb.AppendLine("            </ows:Constraint>");
            sb.AppendLine("          </ows:Get>");
            sb.Append("          <ows:Get xlink:href=\"").Append(EscapeXml(wmtsRestUrlPrefix)).AppendLine("\">");
            sb.AppendLine("            <ows:Constraint name=\"GetEncoding\">");
            sb.AppendLine("              <ows:AllowedValues><ows:Value>RESTful</ows:Value></ows:AllowedValues>");
            sb.AppendLine("            </ows:Constraint>");
            sb.AppendLine("          </ows:Get>");
            sb.AppendLine("        </ows:HTTP>");
            sb.AppendLine("      </ows:DCP>");
            sb.AppendLine("    </ows:Operation>");
            sb.AppendLine("  </ows:OperationsMetadata>");
        }

        if (includeContents)
        {
            sb.AppendLine("  <Contents>");
            foreach (var layer in visibleLayers)
            {
                var layerId = layer.Id.ToString(CultureInfo.InvariantCulture);
                var isQueryable = IsWmtsLayerQueryable(service, layer);
                var dimensions = GetWmtsDimensionDefinitions(layer);
                var dimensionTemplateSuffix = BuildWmtsDimensionTemplateSuffix(
                    dimensions,
                    parameterSeparator: ";",
                    escapeAmpersandsForXmlEmbedding: true);
                var legendDimensionSuffix = BuildWmtsLegendDimensionQuerySuffix(dimensions);
                var tileTemplate = $"{wmtsEndpoint}/{layerId}/{{style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}.png{dimensionTemplateSuffix}";
                var featureInfoTextTemplate = $"{wmtsEndpoint}/{layerId}/{{style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}/{{J}}/{{I}}.txt{dimensionTemplateSuffix}";
                var featureInfoJsonTemplate = $"{wmtsEndpoint}/{layerId}/{{style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}/{{J}}/{{I}}.json{dimensionTemplateSuffix}";
                var legendHref = $"{wmtsEndpoint}?SERVICE=WMTS&REQUEST=GetTile&VERSION={WmtsVersion}&LAYER={layerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0{legendDimensionSuffix}";

                sb.AppendLine("    <Layer>");
                sb.Append("      <ows:Title>").Append(EscapeXml(layer.Name ?? layer.Id.ToString(CultureInfo.InvariantCulture))).AppendLine("</ows:Title>");
                sb.Append("      <ows:Identifier>").Append(layerId).AppendLine("</ows:Identifier>");
                AppendWmtsWgs84BoundingBox(sb, layer);
                sb.AppendLine("      <Style isDefault=\"true\">");
                sb.AppendLine("        <ows:Identifier>default</ows:Identifier>");
                sb.Append("        <LegendURL format=\"image/png\" xlink:href=\"")
                    .Append(EscapeXml(legendHref))
                    .Append("\" width=\"")
                    .Append(TileSize.ToString(CultureInfo.InvariantCulture))
                    .Append("\" height=\"")
                    .Append(TileSize.ToString(CultureInfo.InvariantCulture))
                    .AppendLine("\" />");
                sb.AppendLine("      </Style>");
                sb.AppendLine("      <Format>image/png</Format>");
                if (isQueryable)
                {
                    sb.AppendLine("      <InfoFormat>text/plain</InfoFormat>");
                    sb.AppendLine("      <InfoFormat>application/json</InfoFormat>");
                }

                AppendWmtsDimensionElements(sb, dimensions);
                sb.AppendLine("      <TileMatrixSetLink>");
                sb.AppendLine("        <TileMatrixSet>WebMercatorQuad</TileMatrixSet>");
                sb.AppendLine("        <TileMatrixSetLimits>");
                for (var z = 0; z <= wmtsMaxZoom; z++)
                {
                    var tileMatrixLimitMax = GetWmtsTileMatrixLimitMax(z);
                    sb.AppendLine("          <TileMatrixLimits>");
                    sb.Append("            <TileMatrix>").Append(z.ToString(CultureInfo.InvariantCulture)).AppendLine("</TileMatrix>");
                    sb.AppendLine("            <MinTileRow>0</MinTileRow>");
                    sb.Append("            <MaxTileRow>").Append(tileMatrixLimitMax.ToString(CultureInfo.InvariantCulture)).AppendLine("</MaxTileRow>");
                    sb.AppendLine("            <MinTileCol>0</MinTileCol>");
                    sb.Append("            <MaxTileCol>").Append(tileMatrixLimitMax.ToString(CultureInfo.InvariantCulture)).AppendLine("</MaxTileCol>");
                    sb.AppendLine("          </TileMatrixLimits>");
                }
                sb.AppendLine("        </TileMatrixSetLimits>");
                sb.AppendLine("      </TileMatrixSetLink>");
                sb.Append("      <ResourceURL format=\"image/png\" resourceType=\"tile\" template=\"")
                    .Append(EscapeXml(tileTemplate))
                    .AppendLine("\" />");
                if (isQueryable)
                {
                    sb.Append("      <ResourceURL format=\"text/plain\" resourceType=\"FeatureInfo\" template=\"")
                        .Append(EscapeXml(featureInfoTextTemplate))
                        .AppendLine("\" />");
                    sb.Append("      <ResourceURL format=\"application/json\" resourceType=\"FeatureInfo\" template=\"")
                        .Append(EscapeXml(featureInfoJsonTemplate))
                        .AppendLine("\" />");
                }

                sb.AppendLine("    </Layer>");
            }

            sb.AppendLine("    <TileMatrixSet>");
            sb.AppendLine("      <ows:Identifier>WebMercatorQuad</ows:Identifier>");
            sb.AppendLine("      <ows:SupportedCRS>urn:ogc:def:crs:EPSG::3857</ows:SupportedCRS>");
            sb.AppendLine("      <WellKnownScaleSet>urn:ogc:def:wkss:OGC:1.0:GoogleMapsCompatible</WellKnownScaleSet>");

            for (var z = 0; z <= wmtsMaxZoom; z++)
            {
                var matrixSize = 1L << z;
                var scaleDenominator = GetWmtsScaleDenominator(z);

                sb.AppendLine("      <TileMatrix>");
                sb.Append("        <ows:Identifier>").Append(z.ToString(CultureInfo.InvariantCulture)).AppendLine("</ows:Identifier>");
                sb.Append("        <ScaleDenominator>").Append(FormatWmtsScaleDenominator(scaleDenominator)).AppendLine("</ScaleDenominator>");
                sb.Append("        <TopLeftCorner>").Append((-WebMercatorOrigin).ToString("F6", CultureInfo.InvariantCulture)).Append(' ').Append(WebMercatorOrigin.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</TopLeftCorner>");
                sb.Append("        <TileWidth>").Append(TileSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</TileWidth>");
                sb.Append("        <TileHeight>").Append(TileSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</TileHeight>");
                sb.Append("        <MatrixWidth>").Append(matrixSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</MatrixWidth>");
                sb.Append("        <MatrixHeight>").Append(matrixSize.ToString(CultureInfo.InvariantCulture)).AppendLine("</MatrixHeight>");
                sb.AppendLine("      </TileMatrix>");
            }

            sb.AppendLine("    </TileMatrixSet>");
            sb.AppendLine("  </Contents>");
        }

        if (includeThemes)
        {
            sb.AppendLine("  <Themes>");
            sb.AppendLine("    <Theme>");
            sb.AppendLine("      <ows:Identifier>default</ows:Identifier>");
            foreach (var layer in visibleLayers)
            {
                sb.Append("      <LayerRef>").Append(layer.Id.ToString(CultureInfo.InvariantCulture)).AppendLine("</LayerRef>");
            }

            sb.AppendLine("    </Theme>");
            sb.AppendLine("  </Themes>");
        }

        if (includeServiceMetadataUrl)
        {
            sb.Append("  <ServiceMetadataURL xlink:href=\"").Append(EscapeXml(serviceMetadataUrl)).AppendLine("\" />");
        }

        sb.AppendLine("</Capabilities>");

        return sb.ToString();
    }

    private static bool IsWmtsLayerQueryable(ServiceDefinition service, LayerDefinition layer)
    {
        if (string.Equals(service.Name, CiteServiceName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(layer.Name, CiteWmtsNonQueryableLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static WmtsDimensionDefinition[] GetWmtsDimensionDefinitions(LayerDefinition layer)
    {
        if (!string.Equals(layer.Name, CiteTerrainLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new WmtsDimensionDefinition(
                Identifier: "elevation",
                Values: ["100", "200", "300"],
                DefaultValue: "100",
                SupportsCurrent: true,
                CurrentValue: "300"),
            new WmtsDimensionDefinition(
                Identifier: "scenario",
                Values: ["winter", "summer"],
                DefaultValue: "winter",
                SupportsCurrent: false,
                CurrentValue: null)
        ];
    }

    private static string BuildWmtsDimensionTemplateSuffix(
        IReadOnlyList<WmtsDimensionDefinition> dimensions,
        string parameterSeparator = "&",
        bool escapeAmpersandsForXmlEmbedding = false)
    {
        if (dimensions.Count == 0)
        {
            return string.Empty;
        }

        var separator = parameterSeparator;
        if (escapeAmpersandsForXmlEmbedding && string.Equals(parameterSeparator, "&", StringComparison.Ordinal))
        {
            separator = "&amp;";
        }

        var suffix = new StringBuilder("?");
        for (var i = 0; i < dimensions.Count; i++)
        {
            if (i > 0)
            {
                suffix.Append(separator);
            }

            var identifier = dimensions[i].Identifier;
            suffix.Append(Uri.EscapeDataString(identifier))
                .Append("={")
                .Append(identifier)
                .Append('}');
        }

        return suffix.ToString();
    }

    private static string BuildWmtsLegendDimensionQuerySuffix(
        IReadOnlyList<WmtsDimensionDefinition> dimensions,
        bool hasExistingQueryParameters = true)
    {
        if (dimensions.Count == 0)
        {
            return string.Empty;
        }

        var suffix = new StringBuilder();
        var appendedCount = 0;
        var firstSeparator = hasExistingQueryParameters ? '&' : '?';
        foreach (var dimension in dimensions)
        {
            var value = dimension.DefaultValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = dimension.Values.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (appendedCount > 0)
            {
                suffix.Append('&');
            }
            else
            {
                suffix.Append(firstSeparator);
            }

            suffix.Append(Uri.EscapeDataString(dimension.Identifier))
                .Append('=')
                .Append(Uri.EscapeDataString(value));
            appendedCount++;
        }

        return appendedCount == 0 ? string.Empty : suffix.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string?>> ParseWmtsRestfulAdditionalQueryParameters(string? rawQueryString)
    {
        if (string.IsNullOrWhiteSpace(rawQueryString))
        {
            yield break;
        }

        var query = rawQueryString[0] == '?' ? rawQueryString[1..] : rawQueryString;
        if (query.Length == 0)
        {
            yield break;
        }

        var queryPairs = query.Split(_wmtsAdditionalQuerySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in queryPairs)
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            if (!TryUnescapeWmtsValue(pair[..separatorIndex], out var key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!TryUnescapeWmtsValue(pair[(separatorIndex + 1)..], out var value))
            {
                continue;
            }

            yield return new KeyValuePair<string, string?>(key, value);
        }
    }

    private static void AppendWmtsDimensionElements(StringBuilder sb, IReadOnlyList<WmtsDimensionDefinition> dimensions)
    {
        foreach (var dimension in dimensions)
        {
            sb.AppendLine("      <Dimension>");
            sb.Append("        <ows:Identifier>").Append(EscapeXml(dimension.Identifier)).AppendLine("</ows:Identifier>");

            if (!string.IsNullOrWhiteSpace(dimension.DefaultValue))
            {
                sb.Append("        <Default>").Append(EscapeXml(dimension.DefaultValue!)).AppendLine("</Default>");
            }

            if (dimension.SupportsCurrent)
            {
                sb.AppendLine("        <Current>true</Current>");
            }

            foreach (var value in dimension.Values)
            {
                sb.Append("        <Value>").Append(EscapeXml(value)).AppendLine("</Value>");
            }

            sb.AppendLine("      </Dimension>");
        }
    }

    private static bool TryValidateWmtsDimensionParameters(
        IQueryCollection query,
        LayerDefinition layer,
        bool includeFeatureInfoParameters,
        out IResult errorResult)
    {
        errorResult = Results.Empty;

        var dimensions = GetWmtsDimensionDefinitions(layer);
        if (dimensions.Length == 0)
        {
            return true;
        }

        var dimensionLookup = dimensions.ToDictionary(dimension => dimension.Identifier, StringComparer.OrdinalIgnoreCase);
        foreach (var key in query.Keys)
        {
            if (IsKnownWmtsQueryParameter(key, includeFeatureInfoParameters))
            {
                continue;
            }

            if (!dimensionLookup.ContainsKey(key))
            {
                errorResult = CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    key,
                    $"Unsupported parameter '{key}'.");
                return false;
            }
        }

        foreach (var dimension in dimensions)
        {
            if (!query.ContainsKey(dimension.Identifier))
            {
                if (string.IsNullOrWhiteSpace(dimension.DefaultValue))
                {
                    errorResult = CreateWmtsExceptionReport(
                        "MissingParameterValue",
                        dimension.Identifier,
                        $"{dimension.Identifier} parameter is required.");
                    return false;
                }

                continue;
            }

            var rawValue = GetQueryValue(query, dimension.Identifier);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                errorResult = CreateWmtsExceptionReport(
                    "MissingParameterValue",
                    dimension.Identifier,
                    $"{dimension.Identifier} parameter value is required.");
                return false;
            }

            if (!TryResolveWmtsDimensionValue(dimension, rawValue, out _))
            {
                errorResult = CreateWmtsExceptionReport(
                    "InvalidParameterValue",
                    dimension.Identifier,
                    $"Invalid value for {dimension.Identifier} parameter.");
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownWmtsQueryParameter(string key, bool includeFeatureInfoParameters)
    {
        if (string.Equals(key, "SERVICE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "REQUEST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "VERSION", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "LAYER", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "STYLE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "FORMAT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "TILEMATRIXSET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "TILEMATRIX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "TILEROW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "TILECOL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!includeFeatureInfoParameters)
        {
            return false;
        }

        return string.Equals(key, "I", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "J", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "INFOFORMAT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "FEATURE_COUNT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveWmtsDimensionValue(
        WmtsDimensionDefinition dimension,
        string rawValue,
        out string resolvedValue)
    {
        resolvedValue = string.Empty;
        var normalized = rawValue.Trim();

        if (string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dimension.DefaultValue))
            {
                return false;
            }

            resolvedValue = dimension.DefaultValue!;
            return true;
        }

        if (string.Equals(normalized, "current", StringComparison.OrdinalIgnoreCase))
        {
            if (!dimension.SupportsCurrent)
            {
                return false;
            }

            resolvedValue = dimension.CurrentValue ??
                dimension.DefaultValue ??
                dimension.Values.FirstOrDefault() ??
                string.Empty;
            return resolvedValue.Length > 0;
        }

        var matching = dimension.Values.FirstOrDefault(value =>
            string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));
        if (matching is null)
        {
            return false;
        }

        resolvedValue = matching;
        return true;
    }

    private static string BuildWmtsMinimalCapabilities()
    {
        var sb = new StringBuilder(256);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<Capabilities xmlns=\"http://www.opengis.net/wmts/1.0\"");
        sb.AppendLine("  xmlns:ows=\"http://www.opengis.net/ows/1.1\"");
        sb.AppendLine("  xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        sb.AppendLine("  xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
        sb.Append("  version=\"").Append(WmtsVersion).Append("\" ")
            .Append("updateSequence=\"").Append(WmtsUpdateSequence).Append("\" ")
            .Append("xsi:schemaLocation=\"")
            .Append(WmtsCapabilitiesSchemaLocation)
            .AppendLine("\">");
        sb.AppendLine("</Capabilities>");
        return sb.ToString();
    }

    private static int CompareUpdateSequence(string? requestedUpdateSequence, string currentUpdateSequence)
    {
        if (string.IsNullOrWhiteSpace(requestedUpdateSequence))
        {
            return -1;
        }

        if (long.TryParse(requestedUpdateSequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedLong) &&
            long.TryParse(currentUpdateSequence, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentLong))
        {
            return requestedLong.CompareTo(currentLong);
        }

        return string.Compare(requestedUpdateSequence, currentUpdateSequence, StringComparison.Ordinal);
    }

    private static string ResolveWmtsCapabilitiesMimeType(string acceptFormats)
    {
        var tokens = acceptFormats
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (string.Equals(token, WmtsMimeType, StringComparison.OrdinalIgnoreCase))
            {
                return WmtsMimeType;
            }

            if (string.Equals(token, WmtsTextXmlMimeType, StringComparison.OrdinalIgnoreCase))
            {
                return WmtsTextXmlMimeType;
            }
        }

        return WmtsMimeType;
    }

    private static double GetWmtsScaleDenominator(int zoom)
    {
        return WmtsGoogleMapsCompatibleScaleDenominator0 / (1L << zoom);
    }

    private static string FormatWmtsScaleDenominator(double value)
    {
        return value.ToString("0.################", CultureInfo.InvariantCulture);
    }

    private static int GetWmtsTileMatrixLimitMax(int tileMatrix)
    {
        return tileMatrix < 0
            ? 0
            : (int)((1L << tileMatrix) - 1);
    }

    private static void AppendWmtsWgs84BoundingBox(StringBuilder sb, LayerDefinition layer)
    {
        if (!TryGetWmtsWgs84BoundingBox(layer, out var lowerCorner, out var upperCorner))
        {
            return;
        }

        sb.AppendLine("      <ows:WGS84BoundingBox>");
        sb.Append("        <ows:LowerCorner>").Append(lowerCorner).AppendLine("</ows:LowerCorner>");
        sb.Append("        <ows:UpperCorner>").Append(upperCorner).AppendLine("</ows:UpperCorner>");
        sb.AppendLine("      </ows:WGS84BoundingBox>");
    }

    private static bool TryGetWmtsWgs84BoundingBox(
        LayerDefinition layer,
        out string lowerCorner,
        out string upperCorner)
    {
        lowerCorner = string.Empty;
        upperCorner = string.Empty;

        if (layer.Extent is null)
        {
            return false;
        }

        var extent = layer.Extent.Value;
        if (!OgcExtentTransformer.TryTransformToCrs84(extent.MinX, extent.MinY, extent.SpatialReference, out var min) ||
            !OgcExtentTransformer.TryTransformToCrs84(extent.MaxX, extent.MaxY, extent.SpatialReference, out var max))
        {
            return false;
        }

        var minLon = Math.Min(min.Lon, max.Lon);
        var minLat = Math.Min(min.Lat, max.Lat);
        var maxLon = Math.Max(min.Lon, max.Lon);
        var maxLat = Math.Max(min.Lat, max.Lat);

        lowerCorner = $"{FormatWmtsCoordinate(minLon)} {FormatWmtsCoordinate(minLat)}";
        upperCorner = $"{FormatWmtsCoordinate(maxLon)} {FormatWmtsCoordinate(maxLat)}";
        return true;
    }

    private static string FormatWmtsCoordinate(double value)
        => value.ToString("0.###############", CultureInfo.InvariantCulture);

    private static int ResolveWmtsMaxZoom(HttpContext context)
    {
        var configuredMaxZoom = context.RequestServices.GetService<IOptions<LimitsOptions>>()?.Value?.Tiles.MaxTileZoom
            ?? WmtsMaxZoom;
        return Math.Clamp(configuredMaxZoom, 0, TileMath.MaxSupportedZoomLevel);
    }

    private static bool IsWmtsCapabilitiesAcceptable(string acceptHeader)
    {
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        // TEAM Engine sends this explicit unsupported media type in RESTful HTTP mandatory checks.
        if (acceptHeader.Contains("example/unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasExplicitType = false;
        var hasSupportedExplicitType = false;
        var hasWildcardType = false;

        var mediaTypes = acceptHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var mediaTypeWithParameters in mediaTypes)
        {
            var mediaType = mediaTypeWithParameters.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            if (mediaType.Length == 0)
            {
                continue;
            }

            if (string.Equals(mediaType, "*/*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "application/*", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "text/*", StringComparison.OrdinalIgnoreCase))
            {
                hasWildcardType = true;
                continue;
            }

            hasExplicitType = true;
            if (string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase) ||
                mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            {
                hasSupportedExplicitType = true;
            }
        }

        if (hasSupportedExplicitType)
        {
            return true;
        }

        if (hasExplicitType)
        {
            return false;
        }

        return hasWildcardType;
    }

    private static bool ShouldDisableWmtsCaching(HttpContext context)
    {
        var configuration = context.RequestServices.GetService<IConfiguration>();
        if (configuration == null)
        {
            return false;
        }

        return configuration.GetValue("MapServer:WmtsNoStore", false) ||
            configuration.GetValue("Conformance:DisableWmtsCaching", false);
    }

    private static void ApplyWmtsSyntheticQuery(HttpContext context, IReadOnlyDictionary<string, string?> values)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in context.Request.Query)
        {
            merged[item.Key] = item.Value.ToString();
        }

        foreach (var (key, value) in values)
        {
            merged[key] = value;
        }

        var sb = new StringBuilder();
        foreach (var (key, value) in merged)
        {
            if (sb.Length == 0)
            {
                sb.Append('?');
            }
            else
            {
                sb.Append('&');
            }

            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value ?? string.Empty));
        }

        context.Request.QueryString = new QueryString(sb.ToString());
    }

    private static bool TryParseWmtsResourceSegment(
        string segment,
        string defaultFormat,
        Func<string, string> extensionToFormat,
        out string value,
        out string format)
    {
        value = string.Empty;
        format = defaultFormat;

        if (!TryUnescapeWmtsValue(segment, out var decoded))
        {
            return false;
        }

        if (decoded.Length == 0)
        {
            return true;
        }

        var lastDot = decoded.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == decoded.Length - 1)
        {
            value = decoded;
            return true;
        }

        value = decoded[..lastDot];
        var extension = decoded[(lastDot + 1)..];
        format = extensionToFormat(extension);
        return true;
    }

    private static bool TryUnescapeWmtsValue(string value, out string decoded)
    {
        decoded = string.Empty;

        if (ContainsMalformedEscapeSequence(value))
        {
            return false;
        }

        try
        {
            decoded = Uri.UnescapeDataString(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool ContainsMalformedEscapeSequence(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 >= value.Length)
            {
                return true;
            }

            if (!IsHexDigit(value[i + 1]) || !IsHexDigit(value[i + 2]))
            {
                return true;
            }

            i += 2;
        }

        return false;
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9')
            || (c >= 'A' && c <= 'F')
            || (c >= 'a' && c <= 'f');
    }

    private static string ParseWmtsTileFormatFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            "png" => WmsPngMimeType,
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => $"image/{extension.ToLowerInvariant()}"
        };
    }

    private static string ParseWmtsFeatureInfoFormatFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            "txt" => WmsPlainTextMimeType,
            "json" => WmsJsonMimeType,
            "xml" => "application/xml",
            _ => extension
        };
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static void AppendJsonString(StringBuilder sb, string? value)
    {
        sb.Append('\"');
        if (value is not null)
        {
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\"':
                        sb.Append("\\\"");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < 32)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }
        }

        sb.Append('\"');
    }

    private readonly record struct WmtsDimensionDefinition(
        string Identifier,
        string[] Values,
        string? DefaultValue,
        bool SupportsCurrent,
        string? CurrentValue);
}
