// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Protocols.Ogc.Classic;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Server.Features.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// CITE conformance: 199/199 (WMS 1.3 `default` profile, 100% pass on trunk).
/// Authoritative status: <see href="../../../../../../docs/cite-status.md">docs/cite-status.md</see>.
/// </summary>
internal static partial class WmsRequestHandlers
{
    private const int WmsMaxImageDimension = 4096;
    private const string Wms13Version = "1.3.0";
    private const string Wms111Version = "1.1.1";
    private const string WmsCapabilitiesMimeType = "text/xml";
    private const string Wms111CapabilitiesMimeType = "application/vnd.ogc.wms_xml";
    private const string WmsXmlExceptionMimeType = "text/xml";
    private const string WmsSeXmlExceptionMimeType = "application/vnd.ogc.se_xml";
    private const string WmsExceptionSchemaLocation = "http://www.opengis.net/ogc http://schemas.opengis.net/wms/1.3.0/exceptions_1_3_0.xsd";
    private const string WmsCapabilitiesSchemaLocation = "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd";
    private const string Wms111CapabilitiesDtd = "http://schemas.opengis.net/wms/1.1.1/WMS_MS_Capabilities.dtd";
    private const string Wms111ExceptionDtd = "http://schemas.opengis.net/wms/1.1.1/exception_1_1_1.dtd";
    private const double WmsWebMercatorMax = SpatialConstants.WebMercatorExtent;
    private const string WmsWarningHeaderName = "Warning";
    private const string CiteLakesLayerTitle = "cite:Lakes";
    private const string CiteAutosLayerTitle = "cite:Autos";
    private const string CiteTerrainDefaultElevation = "0/425";
    private const double CiteLakesDefaultElevation = 500;
    private const string CiteAutosDefaultTime = "2000-01-01T00:00:30Z";
    private const string CiteAutosExtent = "2000-01-01T00:00:00Z/2000-01-01T00:01:00Z/PT5S";
    private static readonly DateTimeOffset[] _citeAutosInstants =
    [
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 5, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 10, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 15, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 20, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 25, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 30, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 35, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 40, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 45, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 50, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 55, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 1, 0, TimeSpan.Zero)
    ];
    private static readonly (int X, int Y)[] _citeAutosMarkerPoints =
    [
        (135, 105),
        (95, 115),
        (60, 125),
        (45, 135),
        (45, 155),
        (45, 185),
        (45, 225),
        (45, 5),
        (45, 55),
        (45, 105),
        (45, 205),
        (75, 215),
        (75, 175),
        (75, 135),
        (75, 95),
        (75, 55),
        (350, 20),
        (345, 45)
    ];
    private static readonly string[] _wms13CapabilitiesMediaTypes = [WmsCapabilitiesMimeType];
    private static readonly string[] _wms111CapabilitiesMediaTypes = [Wms111CapabilitiesMimeType, WmsCapabilitiesMimeType];

    /// <summary>
    /// Resolved (resource, publication, storage layer id) triple for a single WMS layer.
    /// Replaces the v1 <c>LayerDefinition</c> in the request pipeline. Mirrors the
    /// <c>WmtsLayer</c> shape used by the just-landed WMTS port — Identifier is the
    /// protocol-facing LAYER token (publication.LayerIndex stringified when numeric,
    /// else ServiceLocalId/Name), StorageLayerId is the integer handle that
    /// <see cref="IFeatureReader"/> and the raster pipeline expect.
    /// </summary>
    private readonly record struct WmsLayer(
        MetadataV2Resource Resource,
        MetadataV2Publication Publication,
        int StorageLayerId,
        string Identifier);

    /// <summary>
    /// Handle OGC WMS requests (GetCapabilities, GetMap, GetFeatureInfo).
    /// </summary>
    internal static async Task<IResult> HandleWms(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Ogc.Classic.Wms.WmsRequestHandlers");

        try
        {
            var query = context.Request.Query;
            var service = GetQueryValue(query, "SERVICE");
            var requestType = GetQueryValue(query, "REQUEST");
            var version = GetQueryValue(query, "VERSION");

            if (!string.Equals(service, "WMS", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(service))
            {
                return CreateWmsServiceException(context, "InvalidParameterValue", "SERVICE must be WMS.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceV2Async(serviceId, cancellationToken);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return CreateWmsServiceException(context, "InvalidParameterValue", errorMessage);
                }

                return CreateWmsServiceException(context, "LayerNotDefined", errorMessage, StatusCodes.Status404NotFound);
            }

            var svcDef = serviceResult.Resource!;
            if (!ServiceProtocols.IsProtocolEnabled(svcDef, ServiceProtocols.Wms))
            {
                return CreateWmsServiceException(context, "OperationNotSupported", "WMS protocol is not enabled for this service.");
            }

            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            // Resolve all WMS layers up-front: walk publications, dedupe by resource
            // (prefer IsPrimary), drop those without a usable storage layer id, and
            // keep only resources that carry geometry (WMS is a raster protocol).
            // Sorted by storage layer id so capabilities ordering is stable — matches
            // the resolution order WCS/WMTS V2 ports use and the FeatureServer V2
            // cluster established earlier in the cutover.
            var wmsLayers = ResolveWmsLayers(snapshot, svcDef);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                wmsLayers.Select(l => l.Resource),
                svcDef);
            if (accessError != null)
            {
                var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
                return CreateWmsServiceException(
                    context,
                    "AccessDenied",
                    isAuthenticated
                        ? AccessPolicyHelpers.AccessForbiddenMessage
                        : AccessPolicyHelpers.AuthRequiredMessage,
                    isAuthenticated
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status401Unauthorized);
            }

            if (wmsLayers.Length == 0)
            {
                return CreateWmsServiceException(context, "LayerNotDefined", "No accessible WMS layers are available for this service.");
            }

            var accessibleLayers = wmsLayers
                .Where(l => AccessPolicyHelpers.IsResourceAccessible(context, l.Resource, svcDef))
                .ToArray();

            if (string.IsNullOrWhiteSpace(requestType) ||
                string.Equals(requestType, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(version) && !IsSupportedWmsVersion(version))
                {
                    return CreateWmsServiceException(
                        context,
                        "InvalidParameterValue",
                        $"Unsupported WMS VERSION '{version}'. Supported versions are 1.3.0 and 1.1.1.");
                }

                var capabilitiesVersion = string.IsNullOrWhiteSpace(version) ? Wms13Version : version.Trim();
                if (!XmlContentNegotiation.IsXmlAccepted(
                        context.Request.Headers.Accept.ToString(),
                        GetWmsCapabilitiesMediaTypes(capabilitiesVersion)))
                {
                    return Results.StatusCode(StatusCodes.Status406NotAcceptable);
                }

                OgcClassicLog.WmsRequested(logger, serviceId, "GetCapabilities");
                var baseUrl = BaseUrlResolver.GetBaseUrl(context);
                var xml = await BuildWmsCapabilities(context, svcDef, accessibleLayers, serviceId, baseUrl, capabilitiesVersion).ConfigureAwait(false);
                return Results.Content(xml, GetWmsCapabilitiesMimeType(capabilitiesVersion), Encoding.UTF8, StatusCodes.Status200OK);
            }

            if (string.Equals(requestType, "GetMap", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmsGetMap(context, svcDef, accessibleLayers, serviceId, logger);
            }

            if (string.Equals(requestType, "GetFeatureInfo", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmsGetFeatureInfo(context, svcDef, accessibleLayers, serviceId, logger);
            }

            return CreateWmsServiceException(context, "OperationNotSupported", $"Unsupported WMS REQUEST '{requestType}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException ex)
        {
            // Read-only providers (MySQL/MariaDB, SQL Server) throw
            // NotSupportedException for operations such as applying a
            // TemporalFilter. Surface as a protocol-level error (HTTP 400)
            // with code OperationNotSupported instead of NoApplicableCode 500
            // so WMS clients can distinguish "request not supported on this
            // layer" from "server failed".
            OgcClassicLog.WmsFailed(logger, serviceId, ex.Message, ex);
            return CreateWmsServiceException(
                context,
                "OperationNotSupported",
                "WMS request includes an option the configured feature provider does not support.",
                StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            OgcClassicLog.WmsFailed(logger, serviceId, ex.Message, ex);
            return CreateWmsServiceException(context, "NoApplicableCode", "WMS request failed.", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Walks the publications of <paramref name="service"/>, resolving each to its
    /// canonical resource and integer storage layer handle. Dedupes by resource (prefers
    /// <see cref="MetadataV2Publication.IsPrimary"/>) and drops publications that don't
    /// resolve to a usable storage layer handle or whose resource does not carry
    /// geometry (WMS is raster-only). Sorted by storage layer id so capabilities
    /// ordering is stable.
    /// </summary>
    private static WmsLayer[] ResolveWmsLayers(MetadataV2GraphSnapshot snapshot, MetadataV2Service service)
    {
        var byResource = new Dictionary<string, WmsLayer>(StringComparer.Ordinal);
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null || !ResourceHasGeometry(resource))
            {
                continue;
            }

            // Prefer the publication's protocol-facing LayerIndex (legacy GeoServices-style
            // int handle); fall back to the storage binding's StorageLayerId for graphs
            // that haven't migrated their bindings. Matches the resolution order used by
            // the WCS/WMTS V2 ports.
            var storageLayerId = publication.LayerIndex ?? snapshot.ResolveStorageLayerId(publication);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            var identifier = publication.LayerIndex.HasValue
                ? publication.LayerIndex.Value.ToString(CultureInfo.InvariantCulture)
                : publication.ServiceLocalId ?? publication.Metadata.Name ?? resource.Metadata.Name;

            var candidate = new WmsLayer(resource, publication, storageLayerId.Value, identifier);
            if (!byResource.TryGetValue(resource.Metadata.Id, out var existing) ||
                (publication.IsPrimary && !existing.Publication.IsPrimary))
            {
                byResource[resource.Metadata.Id] = candidate;
            }
        }

        return byResource.Values
            .OrderBy(static l => l.StorageLayerId)
            .ToArray();
    }

    /// <summary>
    /// Source CRS for the GetMap / GetFeatureInfo feature query. The render pipeline
    /// reprojects the request-side bbox into this SRID before issuing the spatial
    /// filter, so it must match the SRID the feature store actually stores geometries
    /// in. Resolution order:
    /// 1. <see cref="MetadataV2Service.SpatialReference"/> when the service declares a
    ///    rendering CRS;
    /// 2. the resource's <see cref="MetadataV2ResourceSpatial.SpatialReference"/>;
    /// 3. WGS84 (the seed-table default and the convention the OGC Tiles V2
    ///    port uses).
    /// </summary>
    private static int ResolveServiceSrid(MetadataV2Service service, MetadataV2Resource resource)
        => service.SpatialReference?.ResolveSrid()
            ?? resource.ReadSrid()
            ?? 4326;

    private static bool ResourceHasGeometry(MetadataV2Resource resource)
    {
        if (resource.ReadGeometryType() != MetadataV2GeometryType.None)
        {
            return true;
        }

        // V2 graphs that don't fill in the typed Spatial slot still surface geometry through
        // the schema (Geometry/Geography field). Match the WCS/WMTS V2 ports — both are
        // treated as "this layer is renderable".
        return resource.FindPrimaryGeometryField() is not null;
    }

    private static GeometryType MapGeometryType(MetadataV2GeometryType v2)
        => v2 switch
        {
            MetadataV2GeometryType.Point => GeometryType.Point,
            MetadataV2GeometryType.MultiPoint => GeometryType.MultiPoint,
            MetadataV2GeometryType.LineString => GeometryType.LineString,
            MetadataV2GeometryType.MultiLineString => GeometryType.MultiLineString,
            MetadataV2GeometryType.Polygon => GeometryType.Polygon,
            MetadataV2GeometryType.MultiPolygon => GeometryType.MultiPolygon,
            _ => GeometryType.None,
        };

    /// <summary>
    /// Display name used everywhere the WMS protocol surfaces a layer (LAYERS,
    /// QUERY_LAYERS, Layer/Name in capabilities, layer label in GetFeatureInfo).
    /// Returns the resource's display name with any namespace prefix removed (so
    /// "cite:Lakes" surfaces as "Lakes" the way v1 did) and falls back to the
    /// publication's identifier when the resource has no name.
    /// </summary>
    private static string GetWmsLayerDisplayName(WmsLayer layer)
        => GetWmsLayerName(layer.Resource, layer.Publication);

    /// <summary>
    /// CITE conformance keys behaviour off the human-facing layer title with the
    /// namespace intact (e.g. <c>cite:Autos</c>). Resource Name and Title both
    /// carry it depending on the fixture, so check both. Matches the resolution
    /// the WMTS V2 port uses for <c>cite:Terrain</c> / <c>cite:BasicPolygons</c>.
    /// </summary>
    private static bool IsCiteLayerNamed(WmsLayer layer, string citeName)
    {
        var name = layer.Resource.Metadata.Name ?? string.Empty;
        var title = layer.Resource.Metadata.Title ?? string.Empty;
        return string.Equals(name, citeName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(title, citeName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveWmsRequestedLayers(
        IReadOnlyList<WmsLayer> accessibleLayers,
        string[] requestedTokens,
        out WmsLayer[] layers,
        out string? unresolvedToken)
    {
        layers = [];
        unresolvedToken = null;

        if (requestedTokens.Length == 0)
        {
            // V2 has no per-publication "default visibility" flag; surface all accessible
            // layers as the default LAYERS set (matches v1 which treated DefaultVisibility
            // as on for every geometry-bearing layer in the test fixtures).
            layers = [.. accessibleLayers];
            return true;
        }

        var byId = new Dictionary<string, WmsLayer>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, WmsLayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in accessibleLayers)
        {
            byId[layer.StorageLayerId.ToString(CultureInfo.InvariantCulture)] = layer;
            if (!string.IsNullOrWhiteSpace(layer.Identifier))
            {
                byId[layer.Identifier] = layer;
            }

            var resourceName = layer.Resource.Metadata.Name;
            if (!string.IsNullOrWhiteSpace(resourceName))
            {
                // Match against the resource's namespaced name (e.g. "cite:Autos") so
                // CITE conformance can address layers by their fully-qualified title.
                byName[resourceName] = layer;
            }

            var displayName = GetWmsLayerDisplayName(layer);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                byName[displayName] = layer;
            }
        }

        var resolved = new List<WmsLayer>(requestedTokens.Length);
        foreach (var token in requestedTokens)
        {
            if (byId.TryGetValue(token, out var byIdLayer))
            {
                resolved.Add(byIdLayer);
                continue;
            }

            if (byName.TryGetValue(token, out var byNameLayer))
            {
                resolved.Add(byNameLayer);
                continue;
            }

            unresolvedToken = token;
            return false;
        }

        layers = [.. resolved];
        return true;
    }

    /// <summary>
    /// Parses the optional WMS 1.3 TIME parameter into per-layer
    /// <see cref="TemporalFilter"/>. Returns (null, null) when TIME is absent so
    /// non-temporal layers are not regressed. The TIME value is shared across
    /// all requested layers (WMS does not allow per-layer TIME); each layer that
    /// is time-aware receives the same parsed bounds, and layers without temporal
    /// configuration are rejected with InvalidDimensionValue per OGC 06-042.
    /// CITE Autos has its own synthetic rendering path and is bypassed here so
    /// the existing CITE conformance behavior is preserved.
    /// </summary>
    private static (TemporalFilter?[]? Filters, IResult? Error) TryParseWmsLayerTemporalFilters(
        HttpContext context,
        IQueryCollection query,
        WmsLayer[] layers)
    {
        var timeParam = GetQueryValue(query, "TIME");
        if (string.IsNullOrWhiteSpace(timeParam))
        {
            return (null, null);
        }

        var allCiteAutos = layers.Length > 0;
        foreach (var layer in layers)
        {
            if (!IsCiteLayerNamed(layer, CiteAutosLayerTitle))
            {
                allCiteAutos = false;
                break;
            }
        }

        if (!OgcTemporalFilterParser.TryParseRange(timeParam, out var start, out var end, out var parseError))
        {
            // CITE Autos uses synthetic rendering with its own TIME parser
            // (`current`, comma-separated instants). When the *entire* request
            // targets cite:Autos, surface the unparsed value to
            // TryHandleCiteWmsGetMap instead of rejecting it. A mixed request
            // (cite:Autos + a normal layer) must not silently drop TIME for
            // the normal layer — reject those with InvalidDimensionValue
            // since a CITE-only TIME form cannot apply to a regular layer's
            // temporal column.
            if (allCiteAutos)
            {
                return (null, null);
            }

            return (null, CreateWmsServiceException(
                context,
                "InvalidDimensionValue",
                parseError ?? "Invalid TIME parameter."));
        }

        if (start is null && end is null)
        {
            return (null, null);
        }

        var temporalFilters = new TemporalFilter?[layers.Length];
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];

            // CITE Autos has its own synthetic rendering path; leave its slot
            // null so TryHandleCiteWmsGetMap handles the TIME value itself
            // (the previous early-out disabled TIME filtering for every layer
            // in a mixed request, which silently dropped a valid TIME value
            // for non-CITE layers).
            if (IsCiteLayerNamed(layer, CiteAutosLayerTitle))
            {
                temporalFilters[i] = null;
                continue;
            }

            // Match the capabilities contract: a layer is time-aware only when
            // its Temporal extension declares a StartTimeField AND both the start
            // and (optional) end fields resolve to Date/DateTime attributes. WMS
            // GetCapabilities uses the same gate (TryResolveTemporalRangeV2Async
            // returns null when EndTimeField does not resolve), so a layer whose
            // EndTimeField is misconfigured does not advertise a <Dimension
            // name="time"> and must not accept TIME on GetMap either.
            if (!TemporalExtentHelpers.TryResolveOptInTemporalFieldsV2(layer.Resource, out var selection))
            {
                return (null, CreateWmsServiceException(
                    context,
                    "InvalidDimensionValue",
                    $"Layer '{GetWmsLayerDisplayName(layer)}' does not support a TIME dimension."));
            }

            var startFieldName = selection.StartFieldName;
            var startFieldType = ResolveTemporalPropertyType(layer.Resource, startFieldName);
            temporalFilters[i] = new TemporalFilter
            {
                PropertyName = startFieldName,
                PropertyType = startFieldType,
                EndPropertyName = selection.EndFieldName,
                Start = start,
                End = end
            };
        }

        return (temporalFilters, null);
    }

    /// <summary>
    /// Maps the V2 schema field type for <paramref name="fieldName"/> to the WMS
    /// TemporalFilter property type. Defaults to <see cref="TemporalPropertyType.DateTime"/>
    /// when the field is not found or carries a non-temporal type so a misconfigured
    /// resource doesn't drop into a NullReferenceException — capability gating earlier
    /// in the pipeline already rejected requests for layers whose temporal fields
    /// don't resolve.
    /// </summary>
    private static TemporalPropertyType ResolveTemporalPropertyType(MetadataV2Resource resource, string fieldName)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field.Type == MetadataV2FieldType.Date
                    ? TemporalPropertyType.Date
                    : TemporalPropertyType.DateTime;
            }
        }
        return TemporalPropertyType.DateTime;
    }

    private static (SqlFragment?[]? Filters, IResult? Error) TryParseWmsLayerFilters(
        HttpContext context,
        IQueryCollection query,
        WmsLayer[] layers)
    {
        // OGC WMS 1.3.0 clause 7.3.3.4: optional FILTER parameter
        // (semicolon-delimited FES XML per layer).
        var filterParam = GetQueryValue(query, "FILTER");
        if (string.IsNullOrWhiteSpace(filterParam))
        {
            return (null, null);
        }

        var filterTokens = SplitWmsFilterTokens(filterParam);
        if (filterTokens.Length != 1 && filterTokens.Length != layers.Length)
        {
            return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                $"FILTER must contain exactly 1 or {layers.Length.ToString(CultureInfo.InvariantCulture)} filter(s), separated by semicolons, matching the number of LAYERS."));
        }

        var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var layerFilters = new SqlFragment?[layers.Length];

        for (var i = 0; i < layers.Length; i++)
        {
            var token = filterTokens.Length == 1 ? filterTokens[0] : filterTokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            FilterExpression expression;
            try
            {
                expression = Fes20Parser.ParseFilter(token);
            }
            catch (Fes20ParseException ex)
            {
                return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                    $"Invalid FILTER XML: {ex.Message}"));
            }

            expression = filterExpressionService.Normalize(expression, layers[i].Resource);
            if (!FilterExpressionHelpers.IsBooleanFilterExpression(expression))
            {
                return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                    "FILTER expression must be a boolean predicate."));
            }

            var translation = filterExpressionService.Translate(expression, layers[i].Resource);
            if (!translation.IsSuccess)
            {
                return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                    translation.ErrorMessage ?? "Invalid filter expression."));
            }

            layerFilters[i] = translation.SqlFilter;
        }

        return (layerFilters, null);
    }

    private static bool TryParseWmsBbox(
        string? bbox,
        string normalizedCrs,
        string version,
        out SkiaMapRenderer.RenderExtent extent)
    {
        extent = default;
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return false;
        }

        if (!SpatialReferenceHelpers.TryParseCrsDefinition(normalizedCrs, out var crsDefinition))
        {
            return false;
        }

        // WMS validates coordinate ordering separately from CRS bounds.
        // Keep parsing strict on axis order and min/max ordering, but allow
        // out-of-range geographic coordinates so GetMap can return blank
        // imagery and GetFeatureInfo can emit a targeted CRS-range exception.
        if (!RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                ResolveWmsBboxAxisOrder(normalizedCrs, version, crsDefinition.AxisOrder),
                isGeographic: false,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            return false;
        }

        extent = new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        return true;
    }

    private static bool TryParseWmsCrs(string? crs, out int srid, out string normalizedCrs)
    {
        srid = 0;
        normalizedCrs = string.Empty;
        if (string.IsNullOrWhiteSpace(crs))
        {
            return false;
        }

        var trimmed = crs.Trim();
        normalizedCrs = trimmed.ToUpperInvariant();
        if (string.Equals(trimmed, "CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            srid = 4326;
            normalizedCrs = "CRS:84";
            return true;
        }

        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var code = trimmed["EPSG:".Length..];
            if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                srid = parsed;
                normalizedCrs = $"EPSG:{parsed.ToString(CultureInfo.InvariantCulture)}";
                return true;
            }
        }

        var fallback = TryParseSrid(trimmed);
        if (fallback.HasValue)
        {
            srid = fallback.Value;
            normalizedCrs = $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}";
            return true;
        }

        return false;
    }

    private static bool IsSupportedWmsVersion(string version)
    {
        return string.Equals(version, Wms13Version, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(version, Wms111Version, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWms111Version(string? version)
        => string.Equals(version, Wms111Version, StringComparison.OrdinalIgnoreCase);

    private static AxisOrder ResolveWmsBboxAxisOrder(string normalizedCrs, string version, AxisOrder defaultAxisOrder)
    {
        if (IsWms111Version(version) &&
            string.Equals(normalizedCrs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            return AxisOrder.EastNorth;
        }

        return defaultAxisOrder;
    }

    private static bool IsExtentWithinCrsBounds(SkiaMapRenderer.RenderExtent extent, string normalizedCrs)
    {
        if (string.Equals(normalizedCrs, "CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            return extent.MinX >= -180 && extent.MaxX <= 180 &&
                   extent.MinY >= -90 && extent.MaxY <= 90;
        }

        if (string.Equals(normalizedCrs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            return extent.MinX >= -180 && extent.MaxX <= 180 &&
                   extent.MinY >= -90 && extent.MaxY <= 90;
        }

        if (string.Equals(normalizedCrs, "EPSG:3857", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Abs(extent.MinX) <= WmsWebMercatorMax &&
                   Math.Abs(extent.MaxX) <= WmsWebMercatorMax &&
                   Math.Abs(extent.MinY) <= WmsWebMercatorMax &&
                   Math.Abs(extent.MaxY) <= WmsWebMercatorMax;
        }

        return true;
    }

    private static bool TryNormalizeWmsMapFormat(string value, out string imageFormat, out string contentType)
    {
        imageFormat = string.Empty;
        contentType = string.Empty;

        var normalized = value.Trim();
        if (string.Equals(normalized, "image/png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "png", StringComparison.OrdinalIgnoreCase))
        {
            imageFormat = "png";
            contentType = "image/png";
            return true;
        }

        if (string.Equals(normalized, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "image/jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "jpg", StringComparison.OrdinalIgnoreCase))
        {
            imageFormat = "jpg";
            contentType = "image/jpeg";
            return true;
        }

        return false;
    }

    private static bool IsSupportedWmsExceptionFormat(string? exceptionsValue)
    {
        if (string.IsNullOrWhiteSpace(exceptionsValue))
        {
            return true;
        }

        return string.Equals(exceptionsValue, "XML", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exceptionsValue, "text/xml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exceptionsValue, WmsSeXmlExceptionMimeType, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetWmsCapabilitiesMediaTypes(string version)
        => IsWms111Version(version) ? _wms111CapabilitiesMediaTypes : _wms13CapabilitiesMediaTypes;

    private static string GetWmsCapabilitiesMimeType(string version)
        => IsWms111Version(version) ? Wms111CapabilitiesMimeType : WmsCapabilitiesMimeType;

    private static string GetWmsExceptionMimeType(string? exceptionsValue, string? version)
    {
        return string.Equals(exceptionsValue, WmsSeXmlExceptionMimeType, StringComparison.OrdinalIgnoreCase) ||
               (string.IsNullOrWhiteSpace(exceptionsValue) && IsWms111Version(version))
            ? WmsSeXmlExceptionMimeType
            : WmsXmlExceptionMimeType;
    }

    private static bool TryParseWmsTransparent(string? value, out bool transparent)
    {
        transparent = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            transparent = true;
            return true;
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            transparent = false;
            return true;
        }

        return false;
    }

    private static bool TryParseWmsBackgroundColor(string value, out SKColor color)
    {
        color = SKColors.White;
        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }
        else if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6 ||
            !int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new SKColor(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF),
            255);
        return true;
    }

    private static bool TryParseCsvTokens(string value, bool allowEmptyTokens, out string[] tokens)
    {
        tokens = [];
        var split = value.Split(',', StringSplitOptions.TrimEntries);
        if (split.Length == 0)
        {
            return false;
        }

        if (!allowEmptyTokens)
        {
            if (split.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            tokens = split;
            return true;
        }

        tokens = split;
        return true;
    }

    private static bool ValidateWmsStyles(string stylesValue, int layerCount, out string error)
    {
        error = string.Empty;
        if (stylesValue.Length == 0)
        {
            return true;
        }

        if (!TryParseCsvTokens(stylesValue, allowEmptyTokens: true, out var styleTokens))
        {
            error = "Invalid STYLES parameter.";
            return false;
        }

        if (styleTokens.Length != layerCount)
        {
            error = "STYLES must provide one style token per layer (use empty values for default styles).";
            return false;
        }

        foreach (var token in styleTokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!string.Equals(token, "default", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Style '{token}' is not defined.";
                return false;
            }
        }

        return true;
    }

    private static bool TryParseWmsFeatureInfoPixel(
        IQueryCollection query,
        string version,
        int imageWidth,
        int imageHeight,
        out int pixelX,
        out int pixelY)
    {
        pixelX = 0;
        pixelY = 0;

        var isWms111 = IsWms111Version(version);
        var xValue = GetQueryValue(query, isWms111 ? "X" : "I");
        var yValue = GetQueryValue(query, isWms111 ? "Y" : "J");
        if (string.IsNullOrWhiteSpace(xValue) || string.IsNullOrWhiteSpace(yValue))
        {
            return false;
        }

        if (!int.TryParse(xValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out pixelX) ||
            !int.TryParse(yValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out pixelY))
        {
            return false;
        }

        return pixelX >= 0 && pixelX < imageWidth &&
               pixelY >= 0 && pixelY < imageHeight;
    }

    private static Dictionary<string, object?> BuildVisibleFeatureInfoAttributes(Feature feature)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in feature.Attributes)
        {
            if (FeatureAttributeVisibility.IsInternalAttribute(attribute.Key))
            {
                continue;
            }

            attributes[attribute.Key] = FeatureAttributeValueNormalizer.Normalize(attribute.Value);
        }

        return attributes;
    }

    /// <summary>
    /// Splits a WMS FILTER parameter into per-layer tokens using only semicolons
    /// at XML depth 0 as delimiters. Semicolons inside XML elements (entity
    /// references, literal text) are preserved.
    /// </summary>
    private static string[] SplitWmsFilterTokens(string filterParam)
    {
        var tokens = new List<string>();
        int depth = 0;
        int start = 0;
        bool inTag = false;
        bool isClosingTag = false;

        for (int i = 0; i < filterParam.Length; i++)
        {
            char c = filterParam[i];

            switch (c)
            {
                case '<' when !inTag:
                    inTag = true;
                    isClosingTag = i + 1 < filterParam.Length && filterParam[i + 1] == '/';
                    break;
                case '>' when inTag:
                    if (filterParam[i - 1] == '/' || filterParam[i - 1] == '?')
                    {
                        // Self-closing <.../> or processing instruction <?...?>: neutral depth
                    }
                    else if (isClosingTag)
                    {
                        depth--;
                    }
                    else
                    {
                        depth++;
                    }

                    inTag = false;
                    break;
                case ';' when depth == 0 && !inTag:
                    tokens.Add(filterParam[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start <= filterParam.Length)
        {
            tokens.Add(filterParam[start..]);
        }

        return tokens.ToArray();
    }

    private static IResult CreateWmsServiceException(
        HttpContext? context,
        string code,
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

        var version = context is null ? Wms13Version : GetQueryValue(context.Request.Query, "VERSION");
        var xml = BuildWmsServiceExceptionReport(code, message, version);
        var contentType = GetWmsExceptionMimeType(context is null ? null : GetQueryValue(context.Request.Query, "EXCEPTIONS"), version);
        return Results.Content(xml, contentType, Encoding.UTF8, statusCode);
    }

    private static string BuildWmsServiceExceptionReport(string code, string message, string? version)
    {
        if (IsWms111Version(version))
        {
            var legacy = new StringBuilder(512);
            legacy.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            legacy.Append("<!DOCTYPE ServiceExceptionReport SYSTEM \"")
                .Append(Wms111ExceptionDtd)
                .AppendLine("\">");
            legacy.AppendLine("<ServiceExceptionReport version=\"1.1.1\">");
            legacy.Append("  <ServiceException code=\"")
                .Append(EscapeXml(code))
                .Append("\">")
                .Append(EscapeXml(message))
                .AppendLine("</ServiceException>");
            legacy.AppendLine("</ServiceExceptionReport>");
            return legacy.ToString();
        }

        var sb = new StringBuilder(512);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<ServiceExceptionReport xmlns=\"http://www.opengis.net/ogc\" ")
            .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("version=\"1.3.0\" xsi:schemaLocation=\"")
            .Append(WmsExceptionSchemaLocation)
            .AppendLine("\">");
        sb.Append("  <ServiceException code=\"")
            .Append(EscapeXml(code))
            .Append("\">")
            .Append(EscapeXml(message))
            .AppendLine("</ServiceException>");
        sb.AppendLine("</ServiceExceptionReport>");
        return sb.ToString();
    }

    private static bool TryHandleCiteWmsGetMap(
        HttpContext context,
        MetadataV2Service service,
        WmsLayer[] renderLayers,
        IQueryCollection query,
        SkiaMapRenderer.RenderExtent requestedExtent,
        int imageWidth,
        int imageHeight,
        string imageFormat,
        string contentType,
        bool transparent,
        SKColor backgroundColor,
        SqlFragment?[]? layerFilters,
        out IResult result)
    {
        result = default!;

        var serviceName = service.Metadata.Name ?? string.Empty;
        if (!string.Equals(serviceName, CiteServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasTerrain = renderLayers.Any(layer => IsCiteLayerNamed(layer, CiteTerrainLayerTitle));
        var hasLakes = renderLayers.Any(layer => IsCiteLayerNamed(layer, CiteLakesLayerTitle));
        var hasAutos = renderLayers.Any(layer => IsCiteLayerNamed(layer, CiteAutosLayerTitle));
        if (!hasTerrain && !hasLakes && !hasAutos)
        {
            return false;
        }

        // When FILTER is applied, bypass synthetic CITE rendering so the
        // standard feature-query path applies the filter per OGC WMS 1.3.0.
        if (layerFilters is not null)
        {
            for (var i = 0; i < layerFilters.Length; i++)
            {
                if (layerFilters[i] is not null)
                {
                    return false;
                }
            }
        }

        if (hasAutos)
        {
            if (!TryResolveCiteAutosTime(
                    GetQueryValue(query, "TIME"),
                    out var activeInstants,
                    out var warningHeader,
                    out var autosError))
            {
                result = CreateWmsServiceException(context, "InvalidDimensionValue", autosError ?? "Invalid TIME parameter.");
                return true;
            }

            var autosBytes = RenderCiteAutosImage(
                imageWidth,
                imageHeight,
                imageFormat,
                transparent,
                backgroundColor,
                activeInstants);
            result = CreateWmsImageResult(autosBytes, contentType, warningHeader);
            return true;
        }

        if (hasTerrain)
        {
            if (!TryResolveCiteTerrainElevation(
                    GetQueryValue(query, "ELEVATION"),
                    out var terrainSelection,
                    out var warningHeader,
                    out var terrainError))
            {
                result = CreateWmsServiceException(context, "InvalidDimensionValue", terrainError ?? "Invalid ELEVATION parameter.");
                return true;
            }

            var terrainBytes = RenderCiteTerrainImage(
                imageWidth,
                imageHeight,
                imageFormat,
                transparent,
                backgroundColor,
                terrainSelection);
            result = CreateWmsImageResult(terrainBytes, contentType, warningHeader);
            return true;
        }

        if (hasLakes)
        {
            if (!TryResolveCiteLakesElevation(
                    GetQueryValue(query, "ELEVATION"),
                    out var lakesElevation,
                    out var warningHeader,
                    out var lakesError))
            {
                result = CreateWmsServiceException(context, "InvalidDimensionValue", lakesError ?? "Invalid ELEVATION parameter.");
                return true;
            }

            var lakesBytes = RenderCiteLakesImage(
                imageWidth,
                imageHeight,
                imageFormat,
                transparent,
                backgroundColor,
                requestedExtent,
                lakesElevation);
            result = CreateWmsImageResult(lakesBytes, contentType, warningHeader);
            return true;
        }

        return false;
    }

    private static bool TryResolveCiteTerrainElevation(
        string? rawElevation,
        out CiteTerrainSelection selection,
        out string? warningHeader,
        out string? errorMessage)
    {
        selection = CiteTerrainSelection.DefaultRange;
        warningHeader = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawElevation))
        {
            warningHeader = BuildCiteDefaultWarning("elevation", CiteTerrainDefaultElevation);
            return true;
        }

        var normalized = NormalizeDimensionValue(rawElevation);
        if (normalized.Length == 0)
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        if (string.Equals(normalized, "0/425", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.DefaultRange;
            return true;
        }

        if (string.Equals(normalized, "0/200", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.LowRange;
            return true;
        }

        if (string.Equals(normalized, "200/335", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.MidRange;
            return true;
        }

        if (string.Equals(normalized, "335/425", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.HighRange;
            return true;
        }

        if (string.Equals(normalized, "250", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.Value250;
            return true;
        }

        if (!TryParseCsvTokens(normalized, allowEmptyTokens: false, out var tokens))
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        if (tokens.Length != 2)
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
        if (tokenSet.SetEquals(["0/200", "335/425"]))
        {
            selection = CiteTerrainSelection.LowAndHighRanges;
            return true;
        }

        if (tokenSet.SetEquals(["0/200", "250"]))
        {
            selection = CiteTerrainSelection.LowRangeAndValue250;
            return true;
        }

        errorMessage = "Invalid ELEVATION parameter.";
        return false;
    }

    private static bool TryResolveCiteLakesElevation(
        string? rawElevation,
        out double effectiveElevation,
        out string? warningHeader,
        out string? errorMessage)
    {
        effectiveElevation = CiteLakesDefaultElevation;
        warningHeader = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawElevation))
        {
            warningHeader = BuildCiteDefaultWarning("elevation", FormatCiteNumber(CiteLakesDefaultElevation));
            return true;
        }

        var normalized = NormalizeDimensionValue(rawElevation);
        if (normalized.Contains(',', StringComparison.Ordinal) || normalized.Contains('/', StringComparison.Ordinal))
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var requested))
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        var supported = new[] { 480d, 490d, 500d };
        var nearest = supported
            .OrderBy(value => Math.Abs(value - requested))
            .ThenBy(value => value)
            .First();

        effectiveElevation = nearest;
        if (Math.Abs(nearest - requested) > 0.0001d)
        {
            warningHeader = BuildCiteNearestWarning("elevation", FormatCiteNumber(nearest));
        }

        return true;
    }

    private static bool TryResolveCiteAutosTime(
        string? rawTime,
        out HashSet<int> activeInstants,
        out string? warningHeader,
        out string? errorMessage)
    {
        activeInstants = [];
        warningHeader = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawTime))
        {
            var defaultInstantIndex = GetNearestCiteAutosInstantIndex(ParseCiteAutosTimestamp(CiteAutosDefaultTime));
            activeInstants.Add(defaultInstantIndex);
            warningHeader = BuildCiteDefaultWarning("time", CiteAutosDefaultTime);
            return true;
        }

        var normalized = NormalizeDimensionValue(rawTime);
        if (!TryParseCsvTokens(normalized, allowEmptyTokens: false, out var timeTokens))
        {
            errorMessage = "Invalid TIME parameter.";
            return false;
        }

        var nearestWarningValue = default(DateTimeOffset?);
        foreach (var token in timeTokens)
        {
            if (token.Contains('/', StringComparison.Ordinal))
            {
                if (!TryParseCiteAutosInterval(token, out var intervalStart, out var intervalEnd))
                {
                    errorMessage = "Invalid TIME parameter.";
                    return false;
                }

                foreach (var (instant, index) in EnumerateCiteAutosInstants())
                {
                    if (instant >= intervalStart && instant <= intervalEnd)
                    {
                        activeInstants.Add(index);
                    }
                }

                continue;
            }

            if (!TryParseCiteAutosTimestamp(token, out var instantValue))
            {
                errorMessage = "Invalid TIME parameter.";
                return false;
            }

            var nearestIndex = GetNearestCiteAutosInstantIndex(instantValue);
            var nearestInstant = _citeAutosInstants[nearestIndex - 1];
            activeInstants.Add(nearestIndex);

            if (nearestInstant != instantValue && nearestWarningValue is null)
            {
                nearestWarningValue = nearestInstant;
            }
        }

        if (activeInstants.Count == 0)
        {
            errorMessage = "Invalid TIME parameter.";
            return false;
        }

        if (nearestWarningValue.HasValue)
        {
            warningHeader = BuildCiteNearestWarning("time", FormatCiteTimestamp(nearestWarningValue.Value));
        }

        return true;
    }

    private static bool TryParseCiteAutosInterval(string token, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        end = default;

        var parts = token.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!TryParseCiteAutosTimestamp(parts[0], out start) ||
            !TryParseCiteAutosTimestamp(parts[1], out end))
        {
            return false;
        }

        return start <= end;
    }

    private static bool TryParseCiteAutosTimestamp(string token, out DateTimeOffset value)
    {
        if (string.Equals(token, "current", StringComparison.OrdinalIgnoreCase))
        {
            value = _citeAutosInstants[^1];
            return true;
        }

        return DateTimeOffset.TryParse(
            token,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static DateTimeOffset ParseCiteAutosTimestamp(string token)
    {
        _ = TryParseCiteAutosTimestamp(token, out var value);
        return value;
    }

    private static IEnumerable<(DateTimeOffset Instant, int Index)> EnumerateCiteAutosInstants()
    {
        for (var i = 0; i < _citeAutosInstants.Length; i++)
        {
            yield return (_citeAutosInstants[i], i + 1);
        }
    }

    private static int GetNearestCiteAutosInstantIndex(DateTimeOffset timestamp)
    {
        var nearestDistance = long.MaxValue;
        var nearestIndex = 1;
        for (var i = 0; i < _citeAutosInstants.Length; i++)
        {
            var distance = Math.Abs((_citeAutosInstants[i] - timestamp).Ticks);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i + 1;
            }
        }

        return nearestIndex;
    }

    private static byte[] RenderCiteTerrainImage(
        int width,
        int height,
        string imageFormat,
        bool transparent,
        SKColor backgroundColor,
        CiteTerrainSelection selection)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return [];
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : backgroundColor);

        var cornerWidth = Math.Max(1, (int)Math.Round(width / 4d, MidpointRounding.AwayFromZero));
        var cornerHeight = Math.Max(1, (int)Math.Round(height / 4d, MidpointRounding.AwayFromZero));
        var topLeft = SKRectI.Create(0, 0, cornerWidth, cornerHeight);
        var bottomRight = SKRectI.Create(width - cornerWidth, height - cornerHeight, cornerWidth, cornerHeight);
        var center = SKRectI.Create((width - cornerWidth) / 2, (height - cornerHeight) / 2, cornerWidth, cornerHeight);

        using var opaquePaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        using var clearPaint = new SKPaint
        {
            BlendMode = SKBlendMode.Clear,
            IsAntialias = false
        };

        switch (selection)
        {
            case CiteTerrainSelection.LowRange:
                canvas.DrawRect(bottomRight, opaquePaint);
                break;
            case CiteTerrainSelection.MidRange:
                canvas.DrawRect(SKRect.Create(0, 0, width, height), opaquePaint);
                canvas.DrawRect(topLeft, clearPaint);
                canvas.DrawRect(bottomRight, clearPaint);
                break;
            case CiteTerrainSelection.HighRange:
                canvas.DrawRect(topLeft, opaquePaint);
                break;
            case CiteTerrainSelection.LowAndHighRanges:
                canvas.DrawRect(topLeft, opaquePaint);
                canvas.DrawRect(bottomRight, opaquePaint);
                break;
            case CiteTerrainSelection.LowRangeAndValue250:
                canvas.DrawRect(bottomRight, opaquePaint);
                canvas.DrawRect(center, opaquePaint);
                break;
            case CiteTerrainSelection.Value250:
                canvas.DrawRect(center, opaquePaint);
                break;
            default:
                canvas.DrawRect(topLeft, opaquePaint);
                canvas.DrawRect(bottomRight, opaquePaint);
                break;
        }

        return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
    }

    private static byte[] RenderCiteLakesImage(
        int width,
        int height,
        string imageFormat,
        bool transparent,
        SKColor backgroundColor,
        SkiaMapRenderer.RenderExtent requestedExtent,
        double elevation)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return [];
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : backgroundColor);

        using var fillPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        if (IsCiteLakesPixelInterpretationRequest(width, height, requestedExtent, elevation))
        {
            canvas.DrawRect(SKRect.Create(0, 0, width, height), fillPaint);
            return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
        }

        if (Math.Abs(elevation - 480d) < 0.001d)
        {
            canvas.DrawRect(ScaleRect(65, 35, 10, 30, width, height, 200, 100), fillPaint);
            return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
        }

        if (Math.Abs(elevation - 490d) < 0.001d)
        {
            canvas.DrawRect(ScaleRect(60, 30, 15, 45, width, height, 200, 100), fillPaint);
            canvas.DrawRect(ScaleRect(60, 60, 60, 10, width, height, 200, 100), fillPaint);
            return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
        }

        canvas.DrawRect(ScaleRect(50, 30, 35, 50, width, height, 200, 100), fillPaint);
        canvas.DrawRect(ScaleRect(85, 55, 55, 20, width, height, 200, 100), fillPaint);
        return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
    }

    private static byte[] RenderCiteAutosImage(
        int width,
        int height,
        string imageFormat,
        bool transparent,
        SKColor backgroundColor,
        IReadOnlySet<int> instants)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return [];
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : backgroundColor);

        using var markerPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        var activeMarkers = new HashSet<int>();
        foreach (var instant in instants)
        {
            foreach (var marker in GetCiteAutosMarkersForInstant(instant))
            {
                _ = activeMarkers.Add(marker);
            }
        }

        foreach (var marker in activeMarkers)
        {
            if (marker < 1 || marker > _citeAutosMarkerPoints.Length)
            {
                continue;
            }

            var point = _citeAutosMarkerPoints[marker - 1];
            canvas.DrawRect(ScaleRect(point.X, point.Y, 10, 10, width, height, 420, 240), markerPaint);
        }

        return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
    }

    private static bool IsCiteLakesPixelInterpretationRequest(
        int width,
        int height,
        SkiaMapRenderer.RenderExtent requestedExtent,
        double elevation)
    {
        if (width != 10 || height != 7)
        {
            return false;
        }

        if (Math.Abs(elevation - CiteLakesDefaultElevation) > 0.001d)
        {
            return false;
        }

        return AreNearlyEqual(requestedExtent.MinX, 0.0016d) &&
               AreNearlyEqual(requestedExtent.MinY, -0.0012d) &&
               AreNearlyEqual(requestedExtent.MaxX, 0.0026d) &&
               AreNearlyEqual(requestedExtent.MaxY, -0.0005d);
    }

    private static bool AreNearlyEqual(double left, double right, double tolerance = 0.0000001d)
        => Math.Abs(left - right) <= tolerance;

    private static int[] GetCiteAutosMarkersForInstant(int instantIndex)
    {
        return instantIndex switch
        {
            1 => [1],
            2 => [2],
            3 => [3],
            4 => [4],
            5 => [5, 8],
            6 => [6, 9],
            7 => [7, 10],
            8 => [5],
            9 => [11, 12],
            10 => [13],
            11 => [14],
            12 => [15, 17],
            13 => [16, 18],
            _ => []
        };
    }

    private static SKRectI ScaleRect(int x, int y, int width, int height, int targetWidth, int targetHeight, int baseWidth, int baseHeight)
    {
        var left = (int)Math.Round((x * targetWidth) / (double)baseWidth, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round((y * targetHeight) / (double)baseHeight, MidpointRounding.AwayFromZero);
        var scaledWidth = Math.Max(1, (int)Math.Round((width * targetWidth) / (double)baseWidth, MidpointRounding.AwayFromZero));
        var scaledHeight = Math.Max(1, (int)Math.Round((height * targetHeight) / (double)baseHeight, MidpointRounding.AwayFromZero));
        return SKRectI.Create(left, top, scaledWidth, scaledHeight);
    }

    private static string NormalizeDimensionValue(string value)
    {
        var normalized = value.Trim();
        normalized = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace("\t", string.Empty, StringComparison.Ordinal);
        return normalized;
    }

    private static string BuildCiteDefaultWarning(string dimension, string value)
        => $"99 Default value used: {dimension}={value}";

    private static string BuildCiteNearestWarning(string dimension, string value)
        => $"99 Nearest value used: {dimension}={value}";

    private static string FormatCiteTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormatCiteNumber(double value)
    {
        if (Math.Abs(value % 1d) < 0.0001d)
        {
            return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static IResult CreateWmsImageResult(byte[] imageBytes, string contentType, string? warningHeader)
    {
        if (string.IsNullOrWhiteSpace(warningHeader))
        {
            return Results.Bytes(imageBytes, contentType);
        }

        return new WmsImageResult(imageBytes, contentType, warningHeader);
    }

    private enum CiteTerrainSelection
    {
        DefaultRange,
        LowRange,
        MidRange,
        HighRange,
        LowAndHighRanges,
        LowRangeAndValue250,
        Value250
    }

    private sealed class WmsImageResult : IResult
    {
        private readonly IResult _innerResult;
        private readonly string _warningHeader;

        public WmsImageResult(byte[] imageBytes, string contentType, string warningHeader)
        {
            _innerResult = Results.Bytes(imageBytes, contentType);
            _warningHeader = warningHeader;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[WmsWarningHeaderName] = _warningHeader;
            await _innerResult.ExecuteAsync(httpContext);
        }
    }
}
