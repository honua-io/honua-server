// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Protocols.Ogc.Classic;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Honua.Server.Features.Infrastructure.Helpers.DelimitedParameterHelpers;
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Server.Features.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wmts;

internal static class WmtsRequestHandlers
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
    internal static async Task<IResult> HandleWmts(HttpContext context)
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
            HonuaTelemetry.Activities.MapRender, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcTiles);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wmts");

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Ogc.Classic.Wmts.WmtsRequestHandlers");
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

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
                    return CreateWmtsExceptionReport(context,
                        "MissingParameterValue",
                        "service",
                        "SERVICE parameter is required.");
                }

                if (!string.Equals(service, "WMTS", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateWmtsExceptionReport(context,
                        "InvalidParameterValue",
                        "service",
                        "SERVICE must be WMTS.");
                }

                if (string.IsNullOrWhiteSpace(requestType))
                {
                    return CreateWmtsExceptionReport(context,
                        "MissingParameterValue",
                        "request",
                        "REQUEST parameter is required.");
                }
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
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
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, svcDef, ServiceProtocols.Wmts);
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
                return CreateWmtsExceptionReport(context,
                    "InvalidParameterValue",
                    "request",
                    $"Unsupported REQUEST value '{requestType}'.");
            }

            if (!IsWmtsCapabilitiesAcceptable(context.Request.Headers.Accept.ToString()))
            {
                return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            }

            var responseMimeType = WmtsMimeType;
            if (request.ContainsKey("ACCEPTFORMATS"))
            {
                var acceptFormats = GetQueryValue(request, "ACCEPTFORMATS");
                if (string.IsNullOrWhiteSpace(acceptFormats))
                {
                    return CreateWmtsExceptionReport(context,
                        "MissingParameterValue",
                        "acceptFormats",
                        "ACCEPTFORMATS parameter value is required.");
                }

                if (HasEmptyCommaSeparatedToken(acceptFormats))
                {
                    return CreateWmtsExceptionReport(context,
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
                    return CreateWmtsExceptionReport(context,
                        "InvalidParameterValue",
                        "acceptVersions",
                        "ACCEPTVERSIONS contains an empty version value.");
                }

                var versions = acceptVersions
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!versions.Contains(WmtsVersion, StringComparer.OrdinalIgnoreCase))
                {
                    return CreateWmtsExceptionReport(context,
                        "VersionNegotiationFailed",
                        null,
                        "Only WMTS version 1.0.0 is supported.");
                }
            }

            var version = GetQueryValue(request, "VERSION");
            if (!string.IsNullOrWhiteSpace(version) &&
                !string.Equals(version, WmtsVersion, StringComparison.OrdinalIgnoreCase))
            {
                return CreateWmtsExceptionReport(context,
                    "InvalidParameterValue",
                    "version",
                    $"VERSION must be {WmtsVersion}.");
            }

            var updateSequence = GetQueryValue(request, "UPDATESEQUENCE");
            if (request.ContainsKey("UPDATESEQUENCE"))
            {
                if (string.IsNullOrWhiteSpace(updateSequence))
                {
                    return CreateWmtsExceptionReport(context,
                        "MissingParameterValue",
                        "updateSequence",
                        "UPDATESEQUENCE parameter value is required.");
                }

                var updateComparison = CompareUpdateSequence(updateSequence, WmtsUpdateSequence);
                if (updateComparison > 0)
                {
                    return CreateWmtsExceptionReport(context,
                        "InvalidUpdateSequence",
                        null,
                        "UPDATESEQUENCE is greater than the current capabilities update sequence.");
                }

                if (updateComparison == 0)
                {
                    OgcClassicLog.WmtsRequested(logger, serviceId, "GetCapabilities");
                    var minimalXml = BuildWmtsMinimalCapabilities();
                    return Results.Content(minimalXml, responseMimeType);
                }
            }

            var sectionsParam = GetQueryValue(request, "SECTIONS");
            if (request.ContainsKey("SECTIONS") && string.IsNullOrWhiteSpace(sectionsParam))
            {
                return CreateWmtsExceptionReport(context,
                    "MissingParameterValue",
                    "sections",
                    "SECTIONS parameter value is required.");
            }

            if (HasEmptyCommaSeparatedToken(sectionsParam))
            {
                return CreateWmtsExceptionReport(context,
                    "InvalidParameterValue",
                    "sections",
                    "SECTIONS contains an empty section value.");
            }

            if (!TryParseWmtsSections(sectionsParam, out var sections, out var sectionsError))
            {
                return CreateWmtsExceptionReport(context,
                    "InvalidParameterValue",
                    "sections",
                    sectionsError ?? "Invalid SECTIONS parameter.");
            }

            OgcClassicLog.WmtsRequested(logger, serviceId, "GetCapabilities");
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var coordinateTransformService = context.RequestServices.GetService<ICoordinateTransformService>();
            var capabilitiesFeatureReader = context.RequestServices.GetService<IFeatureReader>();
            var visibleLayers = svcDef.Layers
                .Where(layer => layer.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, layer, svcDef))
                .ToArray();
            var xml = await BuildWmtsCapabilitiesAsync(
                svcDef,
                visibleLayers,
                serviceId,
                baseUrl,
                sections,
                wmtsMaxZoom,
                coordinateTransformService,
                capabilitiesFeatureReader,
                cancellationToken).ConfigureAwait(false);
            return Results.Content(xml, responseMimeType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcClassicLog.WmtsFailed(logger, serviceId, ex.Message, ex);
            return CreateWmtsExceptionReport(context,
                "NoApplicableCode",
                "request",
                "WMTS request failed.",
                StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Handles WMTS RESTful resources by translating path variables into WMTS KVP parameters.
    /// </summary>
    internal static async Task<IResult> HandleWmtsRestful(HttpContext context)
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
                return CreateWmtsExceptionReport(context,
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
                PngMimeType,
                ParseWmtsTileFormatFromExtension,
                out var tileColValue,
                out var tileFormatMimeType))
        {
            return CreateWmtsExceptionReport(context,
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
                    PlainTextMimeType,
                    ParseWmtsFeatureInfoFormatFromExtension,
                    out var pixelI,
                    out var infoFormatMimeType))
            {
                return CreateWmtsExceptionReport(context,
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
        OgcClassicLog.WmtsRequested(logger, serviceId, "GetTile");

        var query = context.Request.Query;
        var version = GetQueryValue(query, "VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            return CreateWmtsExceptionReport(context,
                "MissingParameterValue",
                "version",
                "VERSION parameter is required.");
        }

        if (!string.Equals(version, WmtsVersion, StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context,
                "InvalidParameterValue",
                "version",
                $"VERSION must be {WmtsVersion}.");
        }

        if (!TryGetRequiredQueryValue(query, "LAYER", out var layerValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "layer", "LAYER parameter is required.");
        }

        if (!TryResolveWmtsLayer(service, layerValue, out var layer))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "layer", "Invalid LAYER parameter.");
        }

        var layerAccessError = AccessPolicyHelpers.RequireLayerAccess(context, layer!, service);
        if (layerAccessError is not null)
        {
            return layerAccessError;
        }

        if (!TryGetRequiredQueryValue(query, "STYLE", out var styleValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "style", "STYLE parameter is required.");
        }

        if (!string.Equals(styleValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "Style", "Only STYLE=default is supported.");
        }

        if (!TryValidateWmtsDimensionParameters(context, query, layer!, includeFeatureInfoParameters: false, out var dimensionError))
        {
            return dimensionError;
        }

        // Build the optional temporal filter from the validated `time` dimension.
        // The validator guarantees the value is parseable, "default", or "current";
        // this resolver maps default/current to the layer's max timestamp via the
        // shared TemporalExtentHelpers so request handling matches what
        // GetCapabilities advertised.
        var tileTemporalCancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var tileTemporalFeatureReader = context.RequestServices.GetService<IFeatureReader>();
        var (tileTemporalFilter, tileTemporalFilterError) = await TryBuildWmtsLayerTemporalFilterAsync(
            context, query, layer!, tileTemporalFeatureReader, tileTemporalCancellationToken).ConfigureAwait(false);
        if (tileTemporalFilterError is not null)
        {
            return tileTemporalFilterError;
        }

        if (!TryGetRequiredQueryValue(query, "FORMAT", out var formatValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "format", "FORMAT parameter is required.");
        }

        if (!string.Equals(formatValue, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "format", "Only FORMAT=image/png is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIXSET", out var tileMatrixSet))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileMatrixSet", "TILEMATRIXSET parameter is required.");
        }

        if (!string.Equals(tileMatrixSet, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileMatrixSet", "Only TILEMATRIXSET=WebMercatorQuad is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIX", out var tileMatrixValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileMatrix", "TILEMATRIX parameter is required.");
        }

        if (!int.TryParse(tileMatrixValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileMatrix) ||
            tileMatrix < 0 ||
            tileMatrix > wmtsMaxZoom)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileMatrix", "Invalid TILEMATRIX parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEROW", out var tileRowValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileRow", "TILEROW parameter is required.");
        }

        if (!int.TryParse(tileRowValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileRow) || tileRow < 0)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileRow", "Invalid TILEROW parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILECOL", out var tileColValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileCol", "TILECOL parameter is required.");
        }

        if (!int.TryParse(tileColValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileCol) || tileCol < 0)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileCol", "Invalid TILECOL parameter.");
        }

        var maxTileIndex = (1L << tileMatrix) - 1;
        if (tileRow > maxTileIndex)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileRow", "TILEROW is outside the valid range for TILEMATRIX.");
        }

        if (tileCol > maxTileIndex)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileCol", "TILECOL is outside the valid range for TILEMATRIX.");
        }

        var tileMatrixLimitMax = GetWmtsTileMatrixLimitMax(tileMatrix);
        if (tileRow > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileRow", "TILEROW is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        if (tileCol > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileCol", "TILECOL is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        var maxFeatures = service.Metadata?.MapServer?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;
        var renderResult = await RenderRasterTileAsync(
            context,
            service,
            [layer!],
            tileMatrix,
            tileRow,
            tileCol,
            maxFeatures,
            tileTemporalCancellationToken,
            tileTemporalFilter is null ? null : [tileTemporalFilter]).ConfigureAwait(false);

        return renderResult.IsSuccess
            ? Results.Bytes(renderResult.ImageBytes, PngMimeType)
            : renderResult.Error!;
    }

    private static async Task<IResult> HandleWmtsGetFeatureInfo(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        ILogger logger,
        int wmtsMaxZoom)
    {
        OgcClassicLog.WmtsRequested(logger, serviceId, "GetFeatureInfo");
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureIdentify, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcTiles);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wmts-getfeatureinfo");

        var query = context.Request.Query;
        var version = GetQueryValue(query, "VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            return CreateWmtsExceptionReport(context,
                "MissingParameterValue",
                "version",
                "VERSION parameter is required.");
        }

        if (!string.Equals(version, WmtsVersion, StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context,
                "InvalidParameterValue",
                "version",
                $"VERSION must be {WmtsVersion}.");
        }

        if (!TryGetRequiredQueryValue(query, "LAYER", out var layerValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "layer", "LAYER parameter is required.");
        }

        if (!TryResolveWmtsLayer(service, layerValue, out var layer))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "layer", "Invalid LAYER parameter.");
        }

        var layerAccessError = AccessPolicyHelpers.RequireLayerAccess(context, layer!, service);
        if (layerAccessError is not null)
        {
            return layerAccessError;
        }

        if (!TryGetRequiredQueryValue(query, "STYLE", out var styleValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "style", "STYLE parameter is required.");
        }

        if (!string.Equals(styleValue, "default", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "style", "Only STYLE=default is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "FORMAT", out var formatValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "format", "FORMAT parameter is required.");
        }

        if (!string.Equals(formatValue, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "format", "Only FORMAT=image/png is supported.");
        }

        if (!IsWmtsLayerQueryable(service, layer!))
        {
            return CreateWmtsExceptionReport(context,
                "OperationNotSupported",
                "GetFeatureInfo",
                "GetFeatureInfo is not supported for this layer.",
                StatusCodes.Status501NotImplemented);
        }

        if (!TryValidateWmtsDimensionParameters(context, query, layer!, includeFeatureInfoParameters: true, out var dimensionError))
        {
            return dimensionError;
        }

        var (featureInfoTemporalFilter, featureInfoTemporalFilterError) = await TryBuildWmtsLayerTemporalFilterAsync(
            context,
            query,
            layer!,
            context.RequestServices.GetService<IFeatureReader>(),
            cancellationToken).ConfigureAwait(false);
        if (featureInfoTemporalFilterError is not null)
        {
            return featureInfoTemporalFilterError;
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIXSET", out var tileMatrixSet))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileMatrixSet", "TILEMATRIXSET parameter is required.");
        }

        if (!string.Equals(tileMatrixSet, "WebMercatorQuad", StringComparison.OrdinalIgnoreCase))
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileMatrixSet", "Only TILEMATRIXSET=WebMercatorQuad is supported.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEMATRIX", out var tileMatrixValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileMatrix", "TILEMATRIX parameter is required.");
        }

        if (!int.TryParse(tileMatrixValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileMatrix) ||
            tileMatrix < 0 ||
            tileMatrix > wmtsMaxZoom)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileMatrix", "Invalid TILEMATRIX parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILEROW", out var tileRowValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileRow", "TILEROW parameter is required.");
        }

        if (!int.TryParse(tileRowValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileRow) || tileRow < 0)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileRow", "Invalid TILEROW parameter.");
        }

        if (!TryGetRequiredQueryValue(query, "TILECOL", out var tileColValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "TileCol", "TILECOL parameter is required.");
        }

        if (!int.TryParse(tileColValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tileCol) || tileCol < 0)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "TileCol", "Invalid TILECOL parameter.");
        }

        var maxTileIndex = (1L << tileMatrix) - 1;
        if (tileRow > maxTileIndex)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileRow", "TILEROW is outside the valid range for TILEMATRIX.");
        }

        if (tileCol > maxTileIndex)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileCol", "TILECOL is outside the valid range for TILEMATRIX.");
        }

        var tileMatrixLimitMax = GetWmtsTileMatrixLimitMax(tileMatrix);
        if (tileRow > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileRow", "TILEROW is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        if (tileCol > tileMatrixLimitMax)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "TileCol", "TILECOL is outside the TileMatrixSetLimits for TILEMATRIX.");
        }

        if (!TryGetRequiredQueryValue(query, "I", out var iValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "I", "I parameter is required.");
        }

        if (!int.TryParse(iValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixelI) || pixelI < 0)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "I", "I must be a non-negative integer.");
        }

        if (pixelI >= TileSize)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "I", $"I must be less than {TileSize}.");
        }

        if (!TryGetRequiredQueryValue(query, "J", out var jValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "J", "J parameter is required.");
        }

        if (!int.TryParse(jValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixelJ) || pixelJ < 0)
        {
            return CreateWmtsExceptionReport(context, "InvalidParameterValue", "J", "J must be a non-negative integer.");
        }

        if (pixelJ >= TileSize)
        {
            return CreateWmtsExceptionReport(context, "TileOutOfRange", "J", $"J must be less than {TileSize}.");
        }

        if (!TryGetRequiredQueryValue(query, "INFOFORMAT", out var infoFormatValue))
        {
            return CreateWmtsExceptionReport(context, "MissingParameterValue", "InfoFormat", "INFOFORMAT parameter is required.");
        }

        if (!TryNormalizeFeatureInfoFormat(infoFormatValue, out var infoFormat))
        {
            return CreateWmtsExceptionReport(context,
                "InvalidParameterValue",
                "InfoFormat",
                $"Unsupported INFOFORMAT. Supported values are {PlainTextMimeType} and {JsonMimeType}.");
        }

        var featureCount = DefaultFeatureInfoCount;
        if (query.ContainsKey("FEATURE_COUNT"))
        {
            var featureCountRaw = GetQueryValue(query, "FEATURE_COUNT");
            if (string.IsNullOrWhiteSpace(featureCountRaw) ||
                !int.TryParse(featureCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureCount) ||
                featureCount <= 0)
            {
                return CreateWmtsExceptionReport(context, "InvalidParameterValue", "FEATURE_COUNT", "FEATURE_COUNT must be a positive integer.");
            }
        }

        var matrixWidth = 2.0 * WebMercatorOrigin / (1L << tileMatrix);
        var tileMinX = -WebMercatorOrigin + (tileCol * matrixWidth);
        var tileMaxX = tileMinX + matrixWidth;
        var tileMaxY = WebMercatorOrigin - (tileRow * matrixWidth);
        var tileMinY = tileMaxY - matrixWidth;

        var mapX = tileMinX + (((pixelI + 0.5) / TileSize) * matrixWidth);
        var mapY = tileMaxY - (((pixelJ + 0.5) / TileSize) * matrixWidth);
        var tolerance = Math.Max((matrixWidth / TileSize) * DefaultFeatureInfoTolerancePixels, 0.000001);
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
                cancellationToken);
            if (!clickExtentTransform.IsSuccess)
            {
                return CreateWmtsExceptionReport(context,
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
            Limit = remaining,
            TemporalFilter = featureInfoTemporalFilter
        };

        var queryResult = await featureReader.QueryAsync(layer!.Id, featureQuery, cancellationToken);
        foreach (var item in queryResult.Items)
        {
            if (remaining <= 0)
            {
                break;
            }

            remaining--;
            if (string.Equals(infoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(infoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            if (!hasJsonFeature)
            {
                return Results.Content("{\"type\":\"FeatureInfoResponse\",\"features\":[]}", JsonMimeType);
            }

            jsonText.Append("]}");
            return Results.Content(jsonText.ToString(), JsonMimeType);
        }

        var body = plainText.Length > 0
            ? plainText.ToString().TrimEnd()
            : "No features found.";
        return Results.Content(body, PlainTextMimeType);
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
        HttpContext? context,
        string code,
        string? locator,
        string message,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        if (context is not null)
        {
            context.RequestServices.GetService<RecentErrorBuffer>()?.Record(
                context,
                statusCode,
                code,
                message,
                includeClientErrors: true);
        }

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

    private static async Task<string> BuildWmtsCapabilitiesAsync(
        ServiceDefinition service,
        LayerDefinition[] visibleLayers,
        string serviceId,
        string baseUrl,
        WmtsCapabilitiesSections sections,
        int wmtsMaxZoom,
        ICoordinateTransformService? coordinateTransformService,
        IFeatureReader? featureReader,
        CancellationToken cancellationToken)
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
                var dimensions = await GetWmtsDimensionDefinitionsAsync(
                    layer,
                    featureReader,
                    cancellationToken).ConfigureAwait(false);
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
                await AppendWmtsWgs84BoundingBoxAsync(sb, layer, coordinateTransformService, cancellationToken)
                    .ConfigureAwait(false);
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
            sb.AppendLine("      <ows:SupportedCRS>urn:ogc:def:crs:EPSG:6.18:3:3857</ows:SupportedCRS>");
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
        if (string.Equals(layer.Name, CiteTerrainLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
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

        // Time-aware feature layers advertise a continuous "time" dimension so
        // tile/GetFeatureInfo validation accepts a TIME parameter. The default
        // and extent values are computed asynchronously from the layer's
        // temporal range in GetWmtsDimensionDefinitionsAsync. Only emit the
        // dimension when TimeInfo's configured start (and optional end) field
        // actually resolves to a Date/DateTime attribute — otherwise
        // capabilities would advertise a dimension that the request path
        // cannot fulfill (TryResolveTemporalRangeAsync would return null and
        // OgcTemporalFilterParser would reject any value the dimension
        // validator accepts).
        if (TemporalExtentHelpers.HasOptInTemporalFields(layer))
        {
            return
            [
                new WmtsDimensionDefinition(
                    Identifier: "time",
                    Values: [],
                    DefaultValue: null,
                    SupportsCurrent: true,
                    CurrentValue: null)
            ];
        }

        return [];
    }

    /// <summary>
    /// Returns the dynamic dimension list, resolving the continuous time
    /// dimension default value from the layer's temporal extent for capabilities
    /// rendering. Non-temporal layers skip the database call entirely.
    /// </summary>
    private static async Task<WmtsDimensionDefinition[]> GetWmtsDimensionDefinitionsAsync(
        LayerDefinition layer,
        IFeatureReader? featureReader,
        CancellationToken cancellationToken)
    {
        var staticDimensions = GetWmtsDimensionDefinitions(layer);
        if (staticDimensions.Length == 0 || featureReader is null)
        {
            return staticDimensions;
        }

        var timeInfo = layer.Metadata?.TimeInfo;
        if (timeInfo is null || string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
        {
            return staticDimensions;
        }

        var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer,
            featureReader,
            cancellationToken).ConfigureAwait(false);
        if (range is null || !range.Value.HasExtent || range.Value.Min is null || range.Value.Max is null)
        {
            return staticDimensions;
        }

        var min = TemporalExtentHelpers.FormatOgcTemporalValue(range.Value.Min.Value);
        var max = TemporalExtentHelpers.FormatOgcTemporalValue(range.Value.Max.Value);
        var populated = new WmtsDimensionDefinition(
            Identifier: "time",
            Values: [$"{min}/{max}/PT0S"],
            DefaultValue: max,
            SupportsCurrent: true,
            CurrentValue: max);

        // Replace any stub time dimension already present (from the sync path)
        // with the populated entry; preserve other dimensions in their order.
        var resolved = new WmtsDimensionDefinition[staticDimensions.Length];
        var replaced = false;
        for (var i = 0; i < staticDimensions.Length; i++)
        {
            if (!replaced && string.Equals(staticDimensions[i].Identifier, "time", StringComparison.OrdinalIgnoreCase))
            {
                resolved[i] = populated;
                replaced = true;
            }
            else
            {
                resolved[i] = staticDimensions[i];
            }
        }

        if (!replaced)
        {
            var combined = new WmtsDimensionDefinition[resolved.Length + 1];
            Array.Copy(resolved, combined, resolved.Length);
            combined[^1] = populated;
            return combined;
        }

        return resolved;
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
        HttpContext context,
        IQueryCollection query,
        LayerDefinition layer,
        bool includeFeatureInfoParameters,
        out IResult errorResult)
    {
        errorResult = Results.Empty;

        var dimensions = GetWmtsDimensionDefinitions(layer);

        // Reject unknown query keys (including dimension identifiers such as
        // `time` or `elevation` that the layer does not advertise) even when
        // the layer publishes no dimensions at all. Without this scan a
        // non-time-aware layer would silently accept and ignore `time=` and
        // diverge from the docs/contract that says such requests must return
        // InvalidParameterValue.
        var dimensionLookup = dimensions.ToDictionary(dimension => dimension.Identifier, StringComparer.OrdinalIgnoreCase);
        foreach (var key in query.Keys)
        {
            if (IsKnownWmtsQueryParameter(key, includeFeatureInfoParameters))
            {
                continue;
            }

            if (!dimensionLookup.ContainsKey(key))
            {
                errorResult = CreateWmtsExceptionReport(context,
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
                // The continuous time dimension is optional on tile requests;
                // omitting it returns the layer's full extent as if no temporal
                // filter were applied. Discrete dimensions still require a value.
                if (string.IsNullOrWhiteSpace(dimension.DefaultValue) &&
                    !string.Equals(dimension.Identifier, "time", StringComparison.OrdinalIgnoreCase))
                {
                    errorResult = CreateWmtsExceptionReport(context,
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
                errorResult = CreateWmtsExceptionReport(context,
                    "MissingParameterValue",
                    dimension.Identifier,
                    $"{dimension.Identifier} parameter value is required.");
                return false;
            }

            if (!TryResolveWmtsDimensionValue(dimension, rawValue, out _))
            {
                errorResult = CreateWmtsExceptionReport(context,
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

        // The continuous time dimension is dynamically populated in
        // GetCapabilities (default/current = max timestamp) but the sync
        // validator stub leaves DefaultValue/CurrentValue null. Accept the
        // "default" and "current" tokens here so they pass validation; the
        // request handler resolves them to the actual timestamp via the async
        // TemporalExtentHelpers in TryBuildWmtsLayerTemporalFilterAsync.
        if (string.Equals(dimension.Identifier, "time", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "current", StringComparison.OrdinalIgnoreCase))
            {
                resolvedValue = normalized;
                return true;
            }

            // The continuous time dimension does not enumerate discrete values;
            // accept any RFC 3339 instant or interval and let downstream
            // rendering ignore values that do not intersect data (empty tile).
            if (OgcTemporalFilterParser.TryParseRange(normalized, out var start, out var end, out _) &&
                (start is not null || end is not null))
            {
                resolvedValue = normalized;
                return true;
            }

            return false;
        }

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

    /// <summary>
    /// Builds the optional <see cref="TemporalFilter"/> for a WMTS GetTile or
    /// GetFeatureInfo request from the validated <c>time</c> dimension value.
    /// The validator (<see cref="TryValidateWmtsDimensionParameters"/>) has
    /// already accepted the value as <c>default</c>/<c>current</c> or as an
    /// RFC 3339 instant or interval; this helper resolves <c>default</c>/
    /// <c>current</c> to the layer's max timestamp via
    /// <see cref="TemporalExtentHelpers.TryResolveTemporalRangeAsync"/> so it
    /// matches the dimension that GetCapabilities advertises. CITE Terrain
    /// owns its own non-temporal "time" handling and is bypassed so existing
    /// CITE behavior is preserved. Layers without configured TimeInfo also
    /// bypass — those layers do not advertise the dimension and the validator
    /// would have rejected an unknown parameter.
    /// </summary>
    private static async Task<(TemporalFilter? Filter, IResult? Error)> TryBuildWmtsLayerTemporalFilterAsync(
        HttpContext context,
        IQueryCollection query,
        LayerDefinition layer,
        IFeatureReader? featureReader,
        CancellationToken cancellationToken)
    {
        if (!query.ContainsKey("time"))
        {
            return (null, null);
        }

        var rawValue = GetQueryValue(query, "time");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return (null, null);
        }

        if (string.Equals(layer.Name, CiteTerrainLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var timeInfo = layer.Metadata?.TimeInfo;
        if (timeInfo is null || string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
        {
            return (null, null);
        }

        var normalized = rawValue.Trim();
        var parseInput = normalized;

        // GetCapabilities advertises <Default> and <Current> as the layer's
        // max timestamp; resolve "default"/"current" to that same value here so
        // request handling is consistent with the advertised contract.
        if (string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "current", StringComparison.OrdinalIgnoreCase))
        {
            if (featureReader is null)
            {
                return (null, null);
            }

            var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
                layer, featureReader, cancellationToken).ConfigureAwait(false);
            if (range is null || !range.Value.HasExtent || range.Value.Max is null)
            {
                // Capabilities only advertises default/current when an extent
                // exists, so an empty layer here means apply no filter (full
                // extent) rather than reject — preserves the optional-dimension
                // contract documented in temporal-animation-api.md.
                return (null, null);
            }

            parseInput = TemporalExtentHelpers.FormatOgcTemporalValue(range.Value.Max.Value);
        }

        if (!OgcTemporalFilterParser.TryParse(parseInput, layer, out var parsed, out var parseError))
        {
            return (null, CreateWmtsExceptionReport(context,
                "InvalidParameterValue",
                "time",
                parseError ?? "Invalid value for time parameter."));
        }

        return (parsed, null);
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

    private static async Task AppendWmtsWgs84BoundingBoxAsync(
        StringBuilder sb,
        LayerDefinition layer,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var boundingBox = await TryGetWmtsWgs84BoundingBoxAsync(
                layer,
                coordinateTransformService,
                cancellationToken)
            .ConfigureAwait(false);
        if (boundingBox is null)
        {
            return;
        }

        sb.AppendLine("      <ows:WGS84BoundingBox>");
        sb.Append("        <ows:LowerCorner>").Append(boundingBox.Value.LowerCorner).AppendLine("</ows:LowerCorner>");
        sb.Append("        <ows:UpperCorner>").Append(boundingBox.Value.UpperCorner).AppendLine("</ows:UpperCorner>");
        sb.AppendLine("      </ows:WGS84BoundingBox>");
    }

    private static async Task<(string LowerCorner, string UpperCorner)?> TryGetWmtsWgs84BoundingBoxAsync(
        LayerDefinition layer,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        if (layer.Extent is null)
        {
            return null;
        }

        var extent = layer.Extent.Value;
        var transformedExtent = await OgcExtentTransformer
            .TryTransformExtentToCrs84Async(
                extent.MinX,
                extent.MinY,
                extent.MaxX,
                extent.MaxY,
                extent.SpatialReference,
                coordinateTransformService,
                cancellationToken)
            .ConfigureAwait(false);
        if (transformedExtent is null)
        {
            return null;
        }

        var minLon = Math.Min(transformedExtent.Value.MinLon, transformedExtent.Value.MaxLon);
        var minLat = Math.Min(transformedExtent.Value.MinLat, transformedExtent.Value.MaxLat);
        var maxLon = Math.Max(transformedExtent.Value.MinLon, transformedExtent.Value.MaxLon);
        var maxLat = Math.Max(transformedExtent.Value.MinLat, transformedExtent.Value.MaxLat);

        return (
            $"{FormatWmtsCoordinate(minLon)} {FormatWmtsCoordinate(minLat)}",
            $"{FormatWmtsCoordinate(maxLon)} {FormatWmtsCoordinate(maxLat)}");
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
            if (IsRejectedByQuality(mediaTypeWithParameters))
            {
                continue;
            }

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

    private static bool IsRejectedByQuality(string mediaTypeWithParameters)
    {
        var parameters = mediaTypeWithParameters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var parameter in parameters.Skip(1))
        {
            var parts = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !string.Equals(parts[0], "q", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quality))
            {
                return quality <= 0;
            }
        }

        return false;
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
            "png" => PngMimeType,
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => $"image/{extension.ToLowerInvariant()}"
        };
    }

    private static string ParseWmtsFeatureInfoFormatFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            "txt" => PlainTextMimeType,
            "json" => JsonMimeType,
            "xml" => "application/xml",
            _ => extension
        };
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
