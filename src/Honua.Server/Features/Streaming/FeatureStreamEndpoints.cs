// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Feature-change streaming endpoints supporting WebSocket and SSE transports
/// on a single logical route. Admin endpoints expose session visibility.
/// </summary>
internal static class FeatureStreamEndpoints
{
    private const int SubscriptionBboxSrid = 4326;
    private const string WebSocketTransport = "WebSocket";
    private const string SseTransport = "SSE";

    public static void MapFeatureStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var streamGroup = endpoints.MapGroup("/api/v{version:apiVersion}/streaming")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Streaming");

        streamGroup.MapGet("/features", HandleFeatureStream)
            .WithDisplayName("Stream Feature Changes")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .WithDescription("Opens a WebSocket or SSE stream of real-time feature-change events. " +
                             "WebSocket: send Upgrade header. SSE: send Accept: text/event-stream. " +
                             "Query params: cursor (resume from cursor), clientLabel, serviceId, " +
                             "layerIds/layers (comma-separated layer filter), bbox (WGS84; requires exactly one layer), filter, filter-lang.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .AllowAnonymous();

        streamGroup.MapGet("/features/capabilities", HandleCapabilities)
            .WithDisplayName("Get Feature Stream Capabilities")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .WithDescription("Returns edition-aware feature-stream transport, filter, replay, and per-layer capability metadata.")
            .Produces<ApiResponse<FeatureStreamCapabilitiesResponse>>()
            .AllowAnonymous();

        // Admin endpoints for session visibility
        var adminGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/streaming/features")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Streaming")
            .RequireAdminAuthorization();

        adminGroup.MapGet("/sessions", HandleListSessions)
            .WithDisplayName("List Feature Stream Sessions")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<ApiResponse<FeatureStreamStatusResponse>>();

        adminGroup.MapDelete("/sessions/{sessionId:guid}", HandleDisconnectSession)
            .WithDisplayName("Disconnect Feature Stream Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Delete]))
            .Produces<ApiResponse<object>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleFeatureStream(
        [FromServices] FeatureStreamDependencies deps,
        ILogger<FeatureStreamEndpointsLog> logger,
        HttpContext context)
    {
        // Determine transport from request headers.
        var isWebSocket = context.WebSockets.IsWebSocketRequest;
        var accept = context.Request.Headers.Accept.ToString();
        var isSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (!isWebSocket && !isSse)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "WebSocket upgrade or Accept: text/event-stream header required.");
        }

        var editionError = RequireProEdition(deps, context, logger);
        if (editionError is not null)
        {
            return editionError;
        }

        // Parse and validate subscription filters before accepting the connection.
        var filterResult = await ParseSubscriptionFilterAsync(deps, logger, context).ConfigureAwait(false);
        if (filterResult.Error is not null)
        {
            return filterResult.Error;
        }

        var isAdmin = IsAdmin(context.User);
        if (!filterResult.HasSubscription && (!isWebSocket || isAdmin))
        {
            var adminError = RequireAdminForUnfilteredStream(context);
            if (adminError is not null)
            {
                return adminError;
            }
        }

        if (isWebSocket)
        {
            await HandleWebSocketStream(
                deps,
                logger,
                context,
                filterResult.Filter,
                addDefaultSubscription: filterResult.HasSubscription || isAdmin).ConfigureAwait(false);
            return Results.Empty;
        }

        await HandleSseStream(deps.SessionManager, deps.EventStore, deps.Options.Value, logger, context, filterResult.Filter).ConfigureAwait(false);
        return Results.Empty;
    }

    private static async Task<IResult> HandleCapabilities(
        [FromServices] FeatureStreamDependencies deps,
        HttpContext context)
    {
        var options = deps.Options.Value;
        var streamDecision = LicenseGate.CheckEntitlement(
            context.RequestServices,
            "streaming.feature-subscriptions");
        var edition = streamDecision.Edition;
        var enabled = streamDecision.IsActive;
        var layers = await deps.LayerCatalog.ListLayersAsync(context.RequestAborted).ConfigureAwait(false);
        var services = await deps.LayerCatalog.ListServicesAsync(context.RequestAborted).ConfigureAwait(false);
        // Resolve the primary service per layer so the access check evaluates
        // both the layer policy and the service policy (matching the OGC API
        // Collections pattern). Filtering with the layer policy alone would
        // expose layers whose service-level policy denies anonymous reads.
        var layerToService = services.Length > 0
            ? LayerValidationHelpers.BuildPrimaryServiceMap(services)
            : (IReadOnlyDictionary<int, ServiceDefinition>)new Dictionary<int, ServiceDefinition>();
        var layerCapabilities = layers
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(
                context,
                layer,
                layerToService.TryGetValue(layer.Id, out var svc) ? svc : null))
            .Select(layer =>
            {
                var timeInfo = layer.Metadata?.TimeInfo;
                var temporalFields = timeInfo is null
                    ? null
                    : new[] { timeInfo.StartTimeField, timeInfo.EndTimeField }
                        .Where(field => !string.IsNullOrWhiteSpace(field))
                        .Select(field => field!)
                        .ToArray();

                return new FeatureStreamLayerCapability
                {
                    LayerId = layer.Id,
                    Name = layer.Name,
                    CanSubscribe = enabled,
                    SupportsSpatialFilters = layer.HasGeometry,
                    SupportsTemporalFilters = timeInfo is not null &&
                        !string.IsNullOrWhiteSpace(timeInfo.StartTimeField),
                    TemporalFields = temporalFields is { Length: > 0 } ? temporalFields : null,
                    Crs = $"EPSG:{layer.SpatialReference.Wkid.ToString(CultureInfo.InvariantCulture)}"
                };
            })
            .ToArray();

        var response = new FeatureStreamCapabilitiesResponse
        {
            Enabled = enabled,
            Edition = edition.ToString(),
            MinimumEdition = HonuaEdition.Pro.ToString(),
            Transports = enabled ? ["websocket", "sse"] : [],
            FilterFamilies = enabled
                ? ["layer", "bbox", "attribute", "temporal"]
                : [],
            ReplaySupported = enabled,
            CursorRetentionLimit = deps.EventOptions.Value.MaxRetainedEvents,
            HeartbeatIntervalSeconds = options.HeartbeatInterval.TotalSeconds,
            MaxConcurrentSessions = options.MaxConcurrentSessions,
            DeleteBeforeImages = enabled,
            Layers = layerCapabilities
        };

        return Results.Json(
            ApiResponse<FeatureStreamCapabilitiesResponse>.CreateSuccess(response),
            FeatureStreamJsonContext.Default.ApiResponseFeatureStreamCapabilitiesResponse);
    }

    private static async Task<(IStreamSubscriptionFilter? Filter, bool HasSubscription, IResult? Error)> ParseSubscriptionFilterAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context)
    {
        var query = context.Request.Query;
        var serviceId = NullIfEmpty(query["serviceId"].ToString());
        var layersParam = NullIfEmpty(query["layers"].ToString());
        var legacyLayerIdsParam = NullIfEmpty(query["layerIds"].ToString());
        var bboxParam = NullIfEmpty(query["bbox"].ToString());
        var bboxCrsParam = NullIfEmpty(query["bboxCrs"].ToString()) ?? NullIfEmpty(query["bbox-crs"].ToString());
        var polygonParam = NullIfEmpty(query["polygon"].ToString()) ?? NullIfEmpty(query["intersects"].ToString());
        var filterParam = NullIfEmpty(query["filter"].ToString());
        var datetimeParam = NullIfEmpty(query["datetime"].ToString()) ?? NullIfEmpty(query["time"].ToString());

        int[]? layerIds = null;
        double[]? bbox = null;
        FilterExpression? attributeFilter = null;
        StreamTemporalFilter? temporalFilter = null;
        bool hasAnyFilter = serviceId is not null;
        ServiceDefinition? service = null;

        if (serviceId is not null)
        {
            service = await deps.LayerCatalog.GetServiceAsync(serviceId, context.RequestAborted).ConfigureAwait(false);
            if (service is null)
            {
                var msg = $"Service '{serviceId}' not found.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }
        }

        if (!string.IsNullOrWhiteSpace(polygonParam))
        {
            const string msg = "polygonIntersects stream filters are not supported by the active feature-change event source.";
            FeatureStreamLog.FilterValidationFailed(logger, msg);
            return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
        }

        // `layers` is the canonical parameter; `layerIds` is a legacy alias. Both
        // must be parsed, validated, and access-checked through the same helper.
        var layerSource = !string.IsNullOrWhiteSpace(layersParam) ? layersParam : legacyLayerIdsParam;
        if (!string.IsNullOrWhiteSpace(layerSource))
        {
            var (parsedIds, layerError) = await ParseAndAuthorizeLayerIdsAsync(
                deps,
                context,
                logger,
                service,
                layerSource).ConfigureAwait(false);
            if (layerError is not null)
            {
                return (null, false, layerError);
            }

            layerIds = parsedIds;
            hasAnyFilter = true;
        }

        if (service is not null && layerIds is null)
        {
            var accessError = RequireAllLayerAccess(context, service);
            if (accessError is not null)
            {
                return (null, false, accessError);
            }
        }

        // Parse bbox (minX,minY,maxX,maxY).
        if (!string.IsNullOrWhiteSpace(bboxParam))
        {
            if (!IsSupportedBboxCrs(bboxCrsParam))
            {
                var msg = $"Unsupported bbox CRS '{bboxCrsParam}'. Feature streams currently accept bbox filters in EPSG:4326 only.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            var parts = bboxParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                var msg = "Invalid bbox: expected 4 comma-separated values (minX,minY,maxX,maxY).";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            bbox = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], CultureInfo.InvariantCulture, out bbox[i]) || !double.IsFinite(bbox[i]))
                {
                    var msg = $"Invalid bbox value '{parts[i]}' at position {i}. Must be a finite number.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
                }
            }

            if (bbox[0] > bbox[2] || bbox[1] > bbox[3])
            {
                var msg = "Invalid bbox: minX must be <= maxX and minY must be <= maxY.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (bbox[0] < -180 || bbox[2] > 180 || bbox[1] < -90 || bbox[3] > 90)
            {
                const string msg = "Invalid bbox: EPSG:4326 longitude must be within [-180,180] and latitude within [-90,90].";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (layerIds is null || layerIds.Length != 1)
            {
                const string msg = "bbox filters require exactly one layer specified via the layers or layerIds parameter.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            var bboxLayer = ResolveLayer(service, layerIds[0]) ??
                await deps.LayerCatalog.GetLayerAsync(layerIds[0], context.RequestAborted).ConfigureAwait(false);
            if (bboxLayer is null)
            {
                var msg = $"Layer {layerIds[0]} not found.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            var (projectedBbox, bboxError) = await TryProjectSubscriptionBboxAsync(
                deps,
                bbox,
                bboxLayer,
                context.RequestAborted).ConfigureAwait(false);
            if (bboxError is not null)
            {
                FeatureStreamLog.FilterValidationFailed(logger, bboxError);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, bboxError));
            }

            bbox = projectedBbox;
            hasAnyFilter = true;
        }

        // Parse attribute filter (CQL2-text).
        if (!string.IsNullOrWhiteSpace(filterParam))
        {
            if (layerIds is null || layerIds.Length != 1)
            {
                const string msg = "attribute filters require exactly one layer for streaming subscriptions.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            var filterLang = query["filter-lang"].ToString();
            if (!TryResolveFilterLanguage(filterLang, out var language, out var filterLangError))
            {
                FeatureStreamLog.FilterValidationFailed(logger, filterLangError);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, filterLangError));
            }

            var parseResult = deps.FilterExpressionService.Parse(language, filterParam);
            if (!parseResult.IsSuccess)
            {
                var msg = $"Invalid filter expression: {parseResult.ErrorMessage}";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (parseResult.Expression is not null)
            {
                // Enforce streaming depth limit.
                if (InMemoryFilterEvaluator.ExceedsMaxDepth(parseResult.Expression))
                {
                    var msg = $"Filter expression exceeds maximum depth ({InMemoryFilterEvaluator.MaxStreamingDepth}) for streaming subscriptions.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                if (!InMemoryFilterEvaluator.TryValidateStreamingExpression(parseResult.Expression, out var validationError))
                {
                    var msg = validationError ?? "Streaming subscriptions do not support the requested filter expression.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                var filterLayer = ResolveLayer(service, layerIds[0]) ??
                    await deps.LayerCatalog.GetLayerAsync(layerIds[0], context.RequestAborted).ConfigureAwait(false);
                if (filterLayer is null)
                {
                    var msg = $"Layer {layerIds[0]} not found.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                if (!TryValidateAttributeFilterFields(parseResult.Expression, filterLayer, out var fieldError))
                {
                    FeatureStreamLog.FilterValidationFailed(logger, fieldError);
                    return (null, false, StandardErrorHelpers.CreateBadRequest(context, fieldError));
                }

                attributeFilter = parseResult.Expression;
                hasAnyFilter = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(datetimeParam))
        {
            if (layerIds is null || layerIds.Length != 1)
            {
                const string msg = "temporal filters require exactly one time-aware layer for streaming subscriptions.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            var temporalLayer = ResolveLayer(service, layerIds[0]) ??
                await deps.LayerCatalog.GetLayerAsync(layerIds[0], context.RequestAborted).ConfigureAwait(false);
            if (temporalLayer is null)
            {
                var msg = $"Layer {layerIds[0]} not found.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (temporalLayer.Metadata?.TimeInfo is not { } timeInfo ||
                string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
            {
                var msg = $"Layer {layerIds[0]} is not time-aware; temporal stream filters require layer timeInfo.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (!OgcTemporalFilterParser.TryParse(datetimeParam, temporalLayer, out var parsedTemporalFilter, out var temporalError) ||
                parsedTemporalFilter is null)
            {
                var msg = temporalError ?? "Invalid datetime parameter.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            temporalFilter = new StreamTemporalFilter(
                parsedTemporalFilter.Value.PropertyName,
                timeInfo.EndTimeField,
                parsedTemporalFilter.Value.Start,
                parsedTemporalFilter.Value.End);
            hasAnyFilter = true;
        }

        if (!hasAnyFilter)
        {
            return (null, false, null);
        }

        var filter = new StreamSubscriptionFilter(
            serviceId: serviceId,
            layerIds: layerIds,
            bbox: bbox,
            attributeFilter: attributeFilter,
            temporalFilter: temporalFilter);
        return (filter, true, null);
    }

    private static bool TryResolveFilterLanguage(string? filterLang, out FilterLanguage language, out string error)
    {
        language = FilterLanguage.Cql2Text;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filterLang) ||
            filterLang.Equals("cql2-text", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (filterLang.Equals("cql2-json", StringComparison.OrdinalIgnoreCase))
        {
            language = FilterLanguage.Cql2Json;
            return true;
        }

        error = $"Unsupported filter language '{filterLang}'.";
        return false;
    }

    private static IResult? RequireProEdition(
        FeatureStreamDependencies deps,
        HttpContext context,
        ILogger logger)
    {
        return LicenseGate.RequireEntitlement(
            context,
            "streaming.feature-subscriptions",
            "Feature streaming",
            logger);
    }

    private static IResult? RequireAdminForUnfilteredStream(HttpContext context)
    {
        if (IsAdmin(context.User))
        {
            return null;
        }

        return context.User.Identity?.IsAuthenticated == true
            ? StandardErrorHelpers.CreateForbidden(
                context,
                "Unfiltered all-layer feature streams require admin access. Subscribe to an explicit service/layer scope.")
            : StandardErrorHelpers.CreateUnauthorized(
                context,
                "Authentication is required to open an unfiltered feature stream.");
    }

    private static bool IsAdmin(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true && user.IsInRole("admin");

    private static LayerDefinition? ResolveLayer(ServiceDefinition? service, int layerId)
        => service?.GetLayer(layerId);

    private static async Task<(int[]? Ids, IResult? Error)> ParseAndAuthorizeLayerIdsAsync(
        FeatureStreamDependencies deps,
        HttpContext context,
        ILogger logger,
        ServiceDefinition? service,
        string layersValue)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var part in layersValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, CultureInfo.InvariantCulture, out var id))
            {
                var msg = $"Invalid layer ID '{part}'. Must be an integer.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        // Reject layers/layerIds inputs that parsed to zero ids (e.g. "," or
        // "  ,  "). Without this guard the caller treats the request as a
        // valid filtered subscription (HasSubscription=true), which skips the
        // unfiltered admin gate in HandleFeatureStream and the all-layer
        // service-access check below. The resulting subscription matches no
        // events but holds an open SSE/WS session and a MaxConcurrentSessions
        // slot — an anonymous DoS surface.
        if (ids.Count == 0)
        {
            const string msg = "Invalid layer filter: layers/layerIds must specify at least one layer ID.";
            FeatureStreamLog.FilterValidationFailed(logger, msg);
            return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
        }

        IReadOnlyDictionary<int, ServiceDefinition>? layerToService = null;
        foreach (var id in ids)
        {
            // When a serviceId was provided, restrict layer ids to that service so a
            // caller cannot piggy-back unrelated layers on an authorized service.
            // When no serviceId was provided, look up the layer's primary service so
            // the access policy check evaluates the service-level policy too.
            LayerDefinition? layer;
            ServiceDefinition? authService;
            if (service is not null)
            {
                layer = ResolveLayer(service, id);
                if (layer is null)
                {
                    var msg = $"Layer {id} is not part of service '{service.Name}'.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                authService = service;
            }
            else
            {
                layer = await deps.LayerCatalog.GetLayerAsync(id, context.RequestAborted).ConfigureAwait(false);
                if (layer is null)
                {
                    var msg = $"Layer {id} not found.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                layerToService ??= await BuildLayerToPrimaryServiceMapAsync(deps, context.RequestAborted).ConfigureAwait(false);
                authService = layerToService.TryGetValue(layer.Id, out var resolved) ? resolved : null;
            }

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, authService);
            if (accessError is not null)
            {
                return (null, accessError);
            }
        }

        return (ids.ToArray(), null);
    }

    private static async Task<IReadOnlyDictionary<int, ServiceDefinition>> BuildLayerToPrimaryServiceMapAsync(
        FeatureStreamDependencies deps,
        CancellationToken cancellationToken)
    {
        var services = await deps.LayerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
        return services.Length == 0
            ? new Dictionary<int, ServiceDefinition>()
            : LayerValidationHelpers.BuildPrimaryServiceMap(services);
    }

    private static IResult? RequireAllLayerAccess(HttpContext context, ServiceDefinition service)
    {
        foreach (var layer in service.Layers)
        {
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
            if (accessError is not null)
            {
                return accessError;
            }
        }

        return null;
    }

    private static bool IsSupportedBboxCrs(string? bboxCrs)
    {
        if (string.IsNullOrWhiteSpace(bboxCrs))
        {
            return true;
        }

        return bboxCrs.Equals("EPSG:4326", StringComparison.OrdinalIgnoreCase) ||
               bboxCrs.Equals("4326", StringComparison.OrdinalIgnoreCase) ||
               bboxCrs.Equals("http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateAttributeFilterFields(
        FilterExpression expression,
        LayerDefinition layer,
        out string error)
    {
        var fields = layer.Fields
            .Select(field => field.Name)
            .Append(layer.ObjectIdFieldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in EnumeratePropertyReferences(expression))
        {
            if (!fields.Contains(property.PropertyName))
            {
                error = $"Unknown field '{property.PropertyName}' in streaming filter for layer {layer.Id}.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static IEnumerable<PropertyReference> EnumeratePropertyReferences(FilterExpression expression)
    {
        switch (expression)
        {
            case PropertyReference property:
                yield return property;
                break;
            case BinaryExpression binary:
                foreach (var property in EnumeratePropertyReferences(binary.Left))
                {
                    yield return property;
                }

                foreach (var property in EnumeratePropertyReferences(binary.Right))
                {
                    yield return property;
                }

                break;
            case UnaryExpression unary:
                foreach (var property in EnumeratePropertyReferences(unary.Operand))
                {
                    yield return property;
                }

                break;
            case ValueList valueList:
                foreach (var value in valueList.Values)
                {
                    foreach (var property in EnumeratePropertyReferences(value))
                    {
                        yield return property;
                    }
                }

                break;
        }
    }

    private static async Task<(double[] Bbox, string? Error)> TryProjectSubscriptionBboxAsync(
        FeatureStreamDependencies deps,
        double[] bbox,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        if (!layer.HasGeometry)
        {
            return (bbox, $"bbox filters are not supported for non-spatial layer {layer.Id}.");
        }

        var layerSrid = layer.SpatialReference.Wkid;
        if (layerSrid <= 0)
        {
            return (bbox, $"Layer {layer.Id} does not define a valid spatial reference.");
        }

        if (layerSrid == SubscriptionBboxSrid)
        {
            return (bbox, null);
        }

        try
        {
            var projectedWkb = await deps.GeometryOperationService.ProjectAsync(
                SpatialFilterHelpers.CreateEnvelopeWkb(bbox[0], bbox[1], bbox[2], bbox[3]),
                SubscriptionBboxSrid,
                layerSrid,
                cancellationToken).ConfigureAwait(false);

            var geometry = WkbReaderCache.Get().Read(projectedWkb);
            if (geometry is null || geometry.IsEmpty)
            {
                return (bbox, $"Unable to project bbox to layer {layer.Id} spatial reference.");
            }

            var env = geometry.EnvelopeInternal;
            return ([env.MinX, env.MinY, env.MaxX, env.MaxY], null);
        }
        catch (ArgumentException ex)
        {
            return (bbox, $"Invalid bbox projection for layer {layer.Id}: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            return (bbox, $"bbox filters do not support projecting layer {layer.Id} to SRID {layerSrid}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return (bbox, $"bbox filters could not be projected for layer {layer.Id}: {ex.Message}");
        }
    }

    private static async Task HandleWebSocketStream(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        IStreamSubscriptionFilter? subscriptionFilter,
        bool addDefaultSubscription)
    {
        var sessionManager = deps.SessionManager;
        var eventStore = deps.EventStore;
        var options = deps.Options.Value;
        var clientLabel = context.Request.Query["clientLabel"].ToString();
        var cursorParam = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorParam, CultureInfo.InvariantCulture, out var c) ? c : null;
        var session = sessionManager.TryCreateSession(
            WebSocketTransport,
            NullIfEmpty(clientLabel),
            subscriptionFilter,
            addDefaultSubscription);
        if (session is null)
        {
            await WriteSessionLimitExceededAsync(context, options.MaxConcurrentSessions).ConfigureAwait(false);
            return;
        }

        using var sessionLease = session;
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        // The default subscription is allocated at session creation and is never replaced
        // (control-frame subscribes always create distinct ids), so its generation is stable
        // for the life of the session. The replay/handoff/poll callbacks below pin this
        // generation when claiming send-time deliveries through the session manager.
        long defaultSubscriptionGeneration = sessionManager.GetDefaultSubscriptionGeneration(session.SessionId);

        await SendWebSocketStatusAsync(
            webSocket,
            session.WriteLock,
            new FeatureStreamStatusFrame
            {
                Status = "connected",
                Message = "Feature stream connected.",
                SessionId = session.SessionId
            },
            context.RequestAborted).ConfigureAwait(false);

        if (subscriptionFilter is not null)
        {
            FeatureStreamLog.SessionCreatedWithFilter(logger, session.SessionId, subscriptionFilter.Summary);
            await SendWebSocketStatusAsync(
                webSocket,
                session.WriteLock,
                new FeatureStreamStatusFrame
                {
                    Status = "subscribed",
                    Message = "Initial subscription accepted.",
                    SessionId = session.SessionId,
                    SubscriptionId = FeatureStreamSessionManager.DefaultSubscriptionId
                },
                context.RequestAborted).ConfigureAwait(false);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);

        // Replay missed events directly to the WebSocket, bypassing the bounded channel
        // so that large replay backlogs are not truncated by the buffer limit.
        // Live broadcasts flow into the channel concurrently; the drain deduplicates
        // using the replay cursor so events are delivered exactly once.
        bool hasReplay = addDefaultSubscription && cursor.HasValue;
        long replayCursor = 0;
        if (hasReplay)
        {
            try
            {
                replayCursor = await ReplayToWebSocketAsync(
                    webSocket,
                    session.WriteLock,
                    eventStore,
                    cursor!.Value,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId,
                    sessionManager,
                    defaultSubscriptionGeneration).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToWebSocketAsync(
                    webSocket,
                    session.WriteLock,
                    eventStore,
                    replayCursor,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId,
                    sessionManager,
                    defaultSubscriptionGeneration).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                return; // Admin disconnect, slow-consumer removal, or request aborted during replay.
            }
            catch (WebSocketException)
            {
                return; // Client disconnected during replay.
            }
        }
        else
        {
            replayCursor = await eventStore.GetCurrentCursorAsync(linkedCts.Token).ConfigureAwait(false);
        }

        // Activate drain with buffer-sized grace for replay sessions so concurrent
        // overflows during the handoff are absorbed instead of disconnecting.
        sessionManager.MarkDrainStarted(session.SessionId,
            hasReplay ? options.MaxBufferPerConnection : 0);

        if (!hasReplay)
        {
            // Fresh live stream — no replay path, nothing to recover.
            sessionManager.ClearDrainGrace(session.SessionId);
        }

        // Convergent handoff: alternately drain the channel and sweep the store
        // until both are simultaneously empty.  Each TryRead pass creates headroom
        // for concurrent broadcasts; each store sweep recovers grace-dropped events.
        // The loop exits only when the channel is empty AND the store has no new
        // events, so ClearDrainGrace runs with an empty channel.
        try
        {
            if (hasReplay)
            {
                long previousCursor;
                do
                {
                    // Drain channel for headroom only — the store sweep below
                    // delivers everything in cursor order including grace-drops.
                    while (session.Reader.TryRead(out _)) { }

                    previousCursor = replayCursor;
                    replayCursor = await ReplayToWebSocketAsync(
                        webSocket,
                        session.WriteLock,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId,
                        sessionManager,
                        defaultSubscriptionGeneration).ConfigureAwait(false);
                } while (replayCursor > previousCursor || session.Reader.TryPeek(out _));
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            return; // Client disconnected during handoff.
        }
        catch (WebSocketException)
        {
            return; // Client disconnected during handoff.
        }

        // The final handoff runs inside the drain task on its first iteration.
        // It alternates draining the channel (creating headroom) and sweeping
        // the store (recovering grace-drops) until both are quiescent, then
        // clears grace so subsequent overflows are genuine slow consumers.
        // For fresh live sessions (no cursor), onFirstRead is null and the first
        // message flows straight to normal delivery.
        var writerTask = WriteWebSocketAsync(webSocket, session, replayCursor, sessionManager, logger, linkedCts.Token,
            onFirstRead: hasReplay
                ? async (cursor, ct) =>
                {
                    bool progress;
                    do
                    {
                        progress = false;

                        // Drain channel for headroom only — the store sweep below
                        // delivers everything in cursor order including grace-drops.
                        while (session.Reader.TryRead(out _)) { }

                        long prev = cursor;
                        cursor = await ReplayToWebSocketAsync(
                            webSocket,
                            session.WriteLock,
                            eventStore,
                            cursor,
                            options.ReplayBatchSize,
                            logger,
                            session.SessionId,
                            ct,
                            subscriptionFilter,
                            FeatureStreamSessionManager.DefaultSubscriptionId,
                            sessionManager,
                            defaultSubscriptionGeneration).ConfigureAwait(false);
                        if (cursor > prev)
                        {
                            progress = true;
                        }
                    } while (progress || session.Reader.TryPeek(out _));

                    sessionManager.ClearDrainGrace(session.SessionId);
                    return cursor;
                }
        : null,
            // The poll closure is always installed so control-frame subscriptions (added
            // after connect via subscribe frames) are also covered by the durable-store
            // sweep — without this, a non-admin session opened with no query filters
            // (addDefaultSubscription:false) would have no recovery path for cross-node
            // events that the broadcast/Redis pub/sub fan-out missed. The default
            // subscription path stays gated on addDefaultSubscription so its writer-side
            // cursor fence remains the single source of truth for that subscription.
            onPoll: async (cursor, ct) =>
            {
                if (addDefaultSubscription)
                {
                    cursor = await ReplayToWebSocketAsync(
                        webSocket,
                        session.WriteLock,
                        eventStore,
                        cursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        ct,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId,
                        sessionManager,
                        defaultSubscriptionGeneration).ConfigureAwait(false);
                }

                foreach (var sub in sessionManager.GetActivePollableSubscriptions(session.SessionId))
                {
                    var advanced = await ReplayToWebSocketAsync(
                        webSocket,
                        session.WriteLock,
                        eventStore,
                        sub.LastPolledCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        ct,
                        sub.Filter,
                        sub.SubscriptionId,
                        sessionManager,
                        sub.Generation).ConfigureAwait(false);
                    if (advanced > sub.LastPolledCursor)
                    {
                        sessionManager.TryAdvanceSubscriptionPollCursor(
                            session.SessionId,
                            sub.SubscriptionId,
                            sub.Generation,
                            advanced);
                    }
                }

                return cursor;
            },
            pollInterval: options.CrossNodeSyncInterval);

        // Receive loop keeps the connection alive and detects client close.
        try
        {
            while (webSocket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
            {
                var receive = await ReceiveWebSocketTextAsync(
                    webSocket,
                    options.MaxControlFrameBytes,
                    linkedCts.Token).ConfigureAwait(false);
                if (receive.CloseRequested)
                {
                    break;
                }

                if (receive.SizeExceeded)
                {
                    await SendWebSocketErrorAsync(
                        webSocket,
                        session.WriteLock,
                        "control-frame-too-large",
                        $"WebSocket control frame exceeded the {options.MaxControlFrameBytes}-byte limit.",
                        null,
                        linkedCts.Token).ConfigureAwait(false);
                    break;
                }

                if (string.IsNullOrWhiteSpace(receive.Text))
                {
                    continue;
                }

                await HandleWebSocketControlAsync(
                    deps,
                    logger,
                    context,
                    webSocket,
                    session,
                    receive.Text,
                    linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Normal shutdown or disconnect.
        }
        catch (WebSocketException ex)
        {
            FeatureStreamLog.WebSocketReceiveEnded(logger, ex, session.SessionId);
        }
        catch (ObjectDisposedException ex)
        {
            FeatureStreamLog.WebSocketReceiveEnded(logger, ex, session.SessionId);
        }

        // Signal writer to stop and wait for it.
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await writerTask.ConfigureAwait(false);

        // Graceful close.
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    session.DisconnectToken.IsCancellationRequested ? "Session disconnected." : "Stream closed.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                FeatureStreamLog.WebSocketCloseFailed(logger, ex, session.SessionId);
            }
            catch (ObjectDisposedException ex)
            {
                FeatureStreamLog.WebSocketCloseFailed(logger, ex, session.SessionId);
            }
        }
    }

    private static async Task WriteWebSocketAsync(
        WebSocket webSocket,
        FeatureStreamSession session,
        long replayCursor,
        FeatureStreamSessionManager sessionManager,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<long>>? onFirstRead = null,
        Func<long, CancellationToken, Task<long>>? onPoll = null,
        TimeSpan? pollInterval = null)
    {
        try
        {
            var effectivePollInterval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(1));
            // Deadline-based poll scheduling: nextPollAt advances by exactly one
            // interval per fire, regardless of how many channel-traffic loop
            // iterations elapsed in between. Without an absolute deadline,
            // continuous local broadcasts win the WhenAny race every iteration
            // and each round resets the poll delay to a fresh full interval,
            // starving the durable-store recovery path indefinitely.
            var nextPollAt = onPoll is null
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow + effectivePollInterval;

            while (!cancellationToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waitToReadTask = session.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                TimeSpan remainingDelay;
                if (onPoll is null)
                {
                    remainingDelay = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    var diff = nextPollAt - DateTimeOffset.UtcNow;
                    remainingDelay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
                }
                var waitForPollTask = Task.Delay(remainingDelay, waitCts.Token);
                var completed = await Task.WhenAny(waitToReadTask, waitForPollTask).ConfigureAwait(false);

                if (completed == waitForPollTask)
                {
                    waitCts.Cancel();
                    try
                    {
                        await waitToReadTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
                    {
                        // Ignore expected cancellation for the alternate waiter.
                    }

                    replayCursor = await onPoll!(replayCursor, cancellationToken).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + effectivePollInterval;
                    continue;
                }

                waitCts.Cancel();
                if (!await waitToReadTask.ConfigureAwait(false))
                {
                    break;
                }

                try
                {
                    await waitForPollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
                {
                    // Ignore expected cancellation for the alternate waiter.
                }

                while (session.Reader.TryRead(out var message))
                {
                    // First dequeue proves the drain is active.  Run the final store
                    // sweep and clear grace while the reader keeps creating headroom.
                    if (onFirstRead is not null)
                    {
                        replayCursor = await onFirstRead(replayCursor, cancellationToken).ConfigureAwait(false);
                        onFirstRead = null;
                    }

                    if (webSocket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    if (message.IsHeartbeat)
                    {
                        var heartbeatPayload = JsonSerializer.SerializeToUtf8Bytes(
                            new FeatureStreamHeartbeat(),
                            FeatureStreamJsonContext.Default.FeatureStreamHeartbeat);
                        await SendWebSocketJsonAsync(webSocket, session.WriteLock, heartbeatPayload, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var subscriptionId = message.Envelope.SubscriptionId ?? FeatureStreamSessionManager.DefaultSubscriptionId;
                    var isDefaultSubscription = string.Equals(
                        subscriptionId,
                        FeatureStreamSessionManager.DefaultSubscriptionId,
                        StringComparison.Ordinal);

                    // Default-subscription cursor fence. The writer's replayCursor tracks
                    // the high-water mark already delivered to the default subscription
                    // through the initial replay, the convergent handoff loop, the
                    // onFirstRead store sweep, the onPoll cross-node poll, and (post-fix)
                    // each successful live send. Send-time dedup
                    // (TryClaimSubscriptionDelivery) keeps the replay-to-live handoff
                    // exactly-once for small replay windows, but the recent-event LRU is
                    // bounded at RecentEventIdCapacity (128). On a high-volume final sweep
                    // a queued copy can arrive after its dedup key has been evicted, and
                    // the writer would re-send it. SSE has the same fence at the SSE drain.
                    // Non-default subscriptions are protected by their pause/unpause/post-
                    // unpause-sweep choreography, so broadcast does not queue frames for
                    // them within their own replay range.
                    if (replayCursor > 0
                        && isDefaultSubscription
                        && message.Envelope.Cursor <= replayCursor)
                    {
                        continue;
                    }

                    // Per-(event, subscription) dedup is claimed at send time so
                    // events that the channel later dropped as pre-drain overflow
                    // remain replayable. The claim also verifies that the
                    // subscription's generation has not advanced — if it did
                    // (unsubscribe or same-id replacement), the queued frame is
                    // stale and must be dropped without claiming dedup so a
                    // future replay for the new generation can still deliver the
                    // event. Whichever path (this writer or the per-subscription
                    // replay) wins the atomic test-and-set sends the frame; the
                    // other observes the recorded key and skips.
                    var claim = sessionManager.TryClaimSubscriptionDelivery(
                        session.SessionId,
                        subscriptionId,
                        message.SubscriptionGeneration,
                        message.Envelope.EventId);
                    if (claim == SubscriptionDeliveryClaim.StaleGeneration)
                    {
                        FeatureStreamLog.StaleSubscriptionFrameDropped(
                            logger,
                            session.SessionId,
                            subscriptionId,
                            message.SubscriptionGeneration);
                        continue;
                    }

                    if (claim == SubscriptionDeliveryClaim.AlreadyDelivered)
                    {
                        continue;
                    }

                    var payload = JsonSerializer.SerializeToUtf8Bytes(
                        message.Envelope,
                        FeatureStreamJsonContext.Default.FeatureStreamEnvelope);

                    await SendWebSocketJsonAsync(webSocket, session.WriteLock, payload, cancellationToken).ConfigureAwait(false);

                    // Watermark advance after a successful live send. The bounded
                    // RecentEventIdCapacity dedup LRU only protects the most recent 128
                    // entries per session; under high-volume live traffic a delivered
                    // event id can be evicted before the next durable poll runs, which
                    // would let the per-subscription store sweep re-emit it. Advancing
                    // the per-subscription poll watermark here scopes the next durable
                    // sweep to events strictly past the live-delivered cursor, preserving
                    // the documented at-least-once contract with the effectively
                    // exactly-once handoff in the small-window case.
                    var deliveredCursor = message.Envelope.Cursor;
                    if (isDefaultSubscription)
                    {
                        if (deliveredCursor > replayCursor)
                        {
                            replayCursor = deliveredCursor;
                        }
                    }
                    else
                    {
                        sessionManager.TryAdvanceSubscriptionPollCursor(
                            session.SessionId,
                            subscriptionId,
                            message.SubscriptionGeneration,
                            deliveredCursor);
                    }
                }

                // Drained the burst. If the poll deadline elapsed during the
                // drain, run the poll inline now — otherwise continuous broadcast
                // traffic could keep us pinned in the read branch and the
                // durable-store sweep would never fire.
                if (onPoll is not null && DateTimeOffset.UtcNow >= nextPollAt)
                {
                    replayCursor = await onPoll(replayCursor, cancellationToken).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + effectivePollInterval;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            // Client gone.
        }
        catch (ObjectDisposedException)
        {
            // Socket already closed.
        }
    }

    private static async Task HandleWebSocketControlAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        WebSocket webSocket,
        FeatureStreamSession session,
        string text,
        CancellationToken cancellationToken)
    {
        FeatureStreamControlMessage? control;
        try
        {
            control = JsonSerializer.Deserialize(
                text,
                FeatureStreamJsonContext.Default.FeatureStreamControlMessage);
        }
        catch (JsonException)
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-json", "Invalid WebSocket control frame JSON.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (control is null || string.IsNullOrWhiteSpace(control.Type))
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-control", "WebSocket control frame requires a type.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (control.Type.Trim().ToLowerInvariant())
        {
            case "ping":
                await SendWebSocketStatusAsync(
                    webSocket,
                    session.WriteLock,
                    new FeatureStreamStatusFrame
                    {
                        Status = "ok",
                        Message = "Feature stream is connected.",
                        SessionId = session.SessionId
                    },
                    cancellationToken).ConfigureAwait(false);
                return;

            case "unsubscribe":
                await HandleWebSocketUnsubscribeAsync(deps.SessionManager, logger, webSocket, session, control, cancellationToken).ConfigureAwait(false);
                return;

            case "subscribe":
                await HandleWebSocketSubscribeAsync(deps, logger, context, webSocket, session, control, cancellationToken).ConfigureAwait(false);
                return;

            default:
                await SendWebSocketErrorAsync(webSocket, session.WriteLock, "unsupported-control", $"Unsupported WebSocket control frame type '{control.Type}'.", null, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static async Task HandleWebSocketSubscribeAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        WebSocket webSocket,
        FeatureStreamSession session,
        FeatureStreamControlMessage control,
        CancellationToken cancellationToken)
    {
        var (filter, error) = await BuildControlSubscriptionFilterAsync(deps, context, control, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-subscription", error, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var options = deps.Options.Value;
        var subscriptionId = string.IsNullOrWhiteSpace(control.SubscriptionId)
            ? Guid.NewGuid().ToString("N")
            : control.SubscriptionId.Trim();

        if (subscriptionId.Length > options.MaxSubscriptionIdLength)
        {
            await SendWebSocketErrorAsync(
                webSocket,
                session.WriteLock,
                "invalid-subscription-id",
                $"subscriptionId exceeds the {options.MaxSubscriptionIdLength}-character limit.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // The default subscription id is reserved for the server-managed
        // session-wide subscription that is allocated at session creation and
        // owns the writer's onPoll/onFirstRead replay cursor. A client subscribe
        // here would replace it with a new generation, leaving the writer's
        // pinned default-subscription generation stale and silently dropping
        // every cross-node poll result.
        if (string.Equals(subscriptionId, FeatureStreamSessionManager.DefaultSubscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            await SendWebSocketErrorAsync(
                webSocket,
                session.WriteLock,
                "invalid-subscription-id",
                $"subscriptionId '{FeatureStreamSessionManager.DefaultSubscriptionId}' is reserved for server-managed subscriptions.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // For no-cursor subscribes capture the store cursor BEFORE TryAddSubscription so
        // the per-subscription poll watermark covers any event committed between this
        // moment and the moment broadcast first sees the new subscription. Without a
        // pre-add snapshot, an event whose Broadcast iteration races ahead of the Add
        // would be missed by both the broadcast (sub not in dict yet) and the poll
        // (watermark already past the event). Dedup at send time handles any overlap
        // when the broadcast did see the sub. With-cursor subscribes do not need this:
        // their post-unpause-sweep advances the watermark to the last delivered cursor.
        var replayCursor = control.Cursor;
        long preAddCursor = 0;
        if (!replayCursor.HasValue)
        {
            try
            {
                preAddCursor = await deps.EventStore.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        // Pause the subscription before replay so the live channel writer cannot
        // queue events that the per-subscription replay path is about to deliver
        // directly. The unpause after replay restores normal live fan-out.
        var addOutcome = deps.SessionManager.TryAddSubscription(session.SessionId, subscriptionId, filter, paused: replayCursor.HasValue);
        switch (addOutcome.Result)
        {
            case AddSubscriptionResult.Added:
                break;
            case AddSubscriptionResult.LimitReached:
                await SendWebSocketErrorAsync(
                    webSocket,
                    session.WriteLock,
                    "subscription-limit-reached",
                    $"Session already has the maximum of {options.MaxSubscriptionsPerSession} subscriptions; unsubscribe before adding more.",
                    subscriptionId,
                    cancellationToken).ConfigureAwait(false);
                return;
            case AddSubscriptionResult.SessionGone:
            default:
                await SendWebSocketErrorAsync(webSocket, session.WriteLock, "session-closed", "Feature stream session is no longer active.", subscriptionId, cancellationToken).ConfigureAwait(false);
                return;
        }

        // Pin the generation we just allocated so replay claims and the writer
        // drain reject any stale-generation frames if a future control frame
        // replaces this same id mid-flight.
        var subscriptionGeneration = addOutcome.Generation;
        FeatureStreamLog.SubscriptionAdded(logger, session.SessionId, subscriptionId, filter?.Summary ?? "all");

        // Seed the per-subscription poll watermark for the no-cursor path so the
        // writer's cross-node sweep starts from the pre-add snapshot. The
        // with-cursor path advances the watermark in the finally block below.
        if (!replayCursor.HasValue && preAddCursor > 0)
        {
            deps.SessionManager.TryAdvanceSubscriptionPollCursor(
                session.SessionId,
                subscriptionId,
                subscriptionGeneration,
                preAddCursor);
        }
        long? currentCursor = null;
        try
        {
            if (replayCursor.HasValue)
            {
                currentCursor = await ReplayToWebSocketAsync(
                    webSocket,
                    session.WriteLock,
                    deps.EventStore,
                    replayCursor.Value,
                    deps.Options.Value.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    cancellationToken,
                    filter,
                    subscriptionId,
                    deps.SessionManager,
                    subscriptionGeneration).ConfigureAwait(false);
            }

            await SendWebSocketStatusAsync(
                webSocket,
                session.WriteLock,
                new FeatureStreamStatusFrame
                {
                    Status = "subscribed",
                    Message = "Subscription accepted.",
                    SessionId = session.SessionId,
                    SubscriptionId = subscriptionId,
                    Cursor = currentCursor
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (replayCursor.HasValue)
            {
                // Convergent paused-state replay: events persisted after the last
                // ReplayToWebSocketAsync batch but before unpause would otherwise
                // be skipped by Broadcast (paused) and missed entirely. Sweep the
                // store one more time while still paused to drain that gap. Loop
                // until the store has no new events past the cursor we already
                // delivered.
                if (currentCursor.HasValue && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        long previousCursor;
                        do
                        {
                            previousCursor = currentCursor.Value;
                            currentCursor = await ReplayToWebSocketAsync(
                                webSocket,
                                session.WriteLock,
                                deps.EventStore,
                                currentCursor.Value,
                                deps.Options.Value.ReplayBatchSize,
                                logger,
                                session.SessionId,
                                cancellationToken,
                                filter,
                                subscriptionId,
                                deps.SessionManager,
                                subscriptionGeneration).ConfigureAwait(false);
                        } while (currentCursor.Value > previousCursor);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Cancellation during catch-up: exit cleanly and let the
                        // unpause below run so the session does not stay stuck.
                    }
                    catch (WebSocketException)
                    {
                        // Client disconnected during catch-up; nothing more to do.
                    }
                }

                // Unpause now. Any event broadcast after this moment is queued
                // via the normal channel path. The atomic per-(event,
                // subscription) dedup in BroadcastLocally and ReplayToWebSocketAsync
                // ensures that a final post-unpause sweep below cannot duplicate
                // events the broadcast already queued.
                deps.SessionManager.TryUnpauseSubscription(session.SessionId, subscriptionId);

                // Final post-unpause sweep: closes the moment-of-unpause race
                // where a broadcast fires while paused (skipped) and is then
                // never re-broadcast. The convergent loop above narrowed the
                // window to a single instant, but the at-least-once contract
                // demands we still cover it. Atomic dedup keeps it exactly-once.
                if (currentCursor.HasValue && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        currentCursor = await ReplayToWebSocketAsync(
                            webSocket,
                            session.WriteLock,
                            deps.EventStore,
                            currentCursor.Value,
                            deps.Options.Value.ReplayBatchSize,
                            logger,
                            session.SessionId,
                            cancellationToken,
                            filter,
                            subscriptionId,
                            deps.SessionManager,
                            subscriptionGeneration).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Normal shutdown.
                    }
                    catch (WebSocketException)
                    {
                        // Client disconnected.
                    }
                }

                // Seed the per-subscription poll watermark to the highest cursor
                // delivered through the per-sub replay path. The writer's cross-node
                // sweep starts from this watermark on each interval — without it,
                // the poll would re-scan the entire replay range.
                if (currentCursor.HasValue && currentCursor.Value > 0)
                {
                    deps.SessionManager.TryAdvanceSubscriptionPollCursor(
                        session.SessionId,
                        subscriptionId,
                        subscriptionGeneration,
                        currentCursor.Value);
                }
            }
        }
    }

    private static async Task HandleWebSocketUnsubscribeAsync(
        FeatureStreamSessionManager sessionManager,
        ILogger logger,
        WebSocket webSocket,
        FeatureStreamSession session,
        FeatureStreamControlMessage control,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(control.SubscriptionId))
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-unsubscribe", "unsubscribe requires subscriptionId.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var subscriptionId = control.SubscriptionId.Trim();

        // Mirror the subscribe-side reservation: clients cannot remove the
        // server-managed default subscription. Without this guard a removal
        // would strand the writer's pinned default-subscription generation,
        // and onPoll cross-node sweeps would silently drop every event.
        if (string.Equals(subscriptionId, FeatureStreamSessionManager.DefaultSubscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            await SendWebSocketErrorAsync(
                webSocket,
                session.WriteLock,
                "invalid-unsubscribe",
                $"subscriptionId '{FeatureStreamSessionManager.DefaultSubscriptionId}' is reserved and cannot be unsubscribed.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!sessionManager.TryRemoveSubscription(session.SessionId, subscriptionId))
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "subscription-not-found", "Subscription was not found.", subscriptionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        FeatureStreamLog.SubscriptionRemoved(logger, session.SessionId, subscriptionId);
        await SendWebSocketStatusAsync(
            webSocket,
            session.WriteLock,
            new FeatureStreamStatusFrame
            {
                Status = "unsubscribed",
                Message = "Subscription removed.",
                SessionId = session.SessionId,
                SubscriptionId = subscriptionId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IStreamSubscriptionFilter? Filter, string? Error)> BuildControlSubscriptionFilterAsync(
        FeatureStreamDependencies deps,
        HttpContext context,
        FeatureStreamControlMessage control,
        CancellationToken cancellationToken)
    {
        var serviceId = NullIfEmpty(control.ServiceId);
        ServiceDefinition? service = null;
        if (serviceId is not null)
        {
            service = await deps.LayerCatalog.GetServiceAsync(serviceId, cancellationToken).ConfigureAwait(false);
            if (service is null)
            {
                return (null, $"Service '{serviceId}' not found.");
            }
        }

        var layerIds = ResolveControlLayerIds(control);
        if (serviceId is null && layerIds is null && !IsAdmin(context.User))
        {
            return (null, "Unfiltered all-layer feature streams require admin access.");
        }

        if (layerIds is not null)
        {
            IReadOnlyDictionary<int, ServiceDefinition>? layerToService = null;
            foreach (var layerId in layerIds)
            {
                // When a serviceId was provided, restrict layer ids to that service so a
                // caller cannot piggy-back unrelated layers on an authorized service.
                // When no serviceId was provided, look up the layer's primary service so
                // the access policy check evaluates the service-level policy too.
                LayerDefinition? layer;
                ServiceDefinition? authService;
                if (service is not null)
                {
                    layer = ResolveLayer(service, layerId);
                    if (layer is null)
                    {
                        return (null, $"Layer {layerId} is not part of service '{service.Name}'.");
                    }

                    authService = service;
                }
                else
                {
                    layer = await deps.LayerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
                    if (layer is null)
                    {
                        return (null, $"Layer {layerId} not found.");
                    }

                    layerToService ??= await BuildLayerToPrimaryServiceMapAsync(deps, cancellationToken).ConfigureAwait(false);
                    authService = layerToService.TryGetValue(layer.Id, out var resolved) ? resolved : null;
                }

                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, authService);
                if (accessError is not null)
                {
                    return (null, "Access to the requested stream layer is forbidden.");
                }
            }
        }
        else if (service is not null)
        {
            var accessError = RequireAllLayerAccess(context, service);
            if (accessError is not null)
            {
                return (null, "Access to the requested stream service is forbidden.");
            }
        }

        double[]? bbox = null;
        if (control.Bbox is not null)
        {
            if (control.Bbox.Length != 4 || control.Bbox.Any(value => !double.IsFinite(value)))
            {
                return (null, "bbox requires four finite values.");
            }

            if (!IsSupportedBboxCrs(control.BboxCrs))
            {
                return (null, "Feature streams currently accept bbox filters in EPSG:4326 only.");
            }

            if (layerIds is null || layerIds.Length != 1)
            {
                return (null, "bbox filters require exactly one layer.");
            }

            if (control.Bbox[0] > control.Bbox[2] || control.Bbox[1] > control.Bbox[3] ||
                control.Bbox[0] < -180 || control.Bbox[2] > 180 ||
                control.Bbox[1] < -90 || control.Bbox[3] > 90)
            {
                return (null, "Invalid EPSG:4326 bbox.");
            }

            var layer = ResolveLayer(service, layerIds[0]) ??
                await deps.LayerCatalog.GetLayerAsync(layerIds[0], cancellationToken).ConfigureAwait(false);
            if (layer is null)
            {
                return (null, $"Layer {layerIds[0]} not found.");
            }

            var projected = await TryProjectSubscriptionBboxAsync(deps, control.Bbox, layer, cancellationToken).ConfigureAwait(false);
            if (projected.Error is not null)
            {
                return (null, projected.Error);
            }

            bbox = projected.Bbox;
        }

        FilterExpression? attributeFilter = null;
        if (!string.IsNullOrWhiteSpace(control.Filter))
        {
            if (layerIds is null || layerIds.Length != 1)
            {
                return (null, "attribute filters require exactly one layer.");
            }

            if (!TryResolveFilterLanguage(control.FilterLang, out var language, out var filterLangError))
            {
                return (null, filterLangError);
            }

            var parseResult = deps.FilterExpressionService.Parse(language, control.Filter);
            if (!parseResult.IsSuccess || parseResult.Expression is null)
            {
                return (null, $"Invalid filter expression: {parseResult.ErrorMessage}");
            }

            if (InMemoryFilterEvaluator.ExceedsMaxDepth(parseResult.Expression))
            {
                return (null, $"Filter expression exceeds maximum depth ({InMemoryFilterEvaluator.MaxStreamingDepth}) for streaming subscriptions.");
            }

            if (!InMemoryFilterEvaluator.TryValidateStreamingExpression(parseResult.Expression, out var validationError))
            {
                return (null, validationError ?? "Streaming subscriptions do not support the requested filter expression.");
            }

            var layer = ResolveLayer(service, layerIds[0]) ??
                await deps.LayerCatalog.GetLayerAsync(layerIds[0], cancellationToken).ConfigureAwait(false);
            if (layer is null)
            {
                return (null, $"Layer {layerIds[0]} not found.");
            }

            if (!TryValidateAttributeFilterFields(parseResult.Expression, layer, out var fieldError))
            {
                return (null, fieldError);
            }

            attributeFilter = parseResult.Expression;
        }

        StreamTemporalFilter? temporalFilter = null;
        if (!string.IsNullOrWhiteSpace(control.Datetime))
        {
            if (layerIds is null || layerIds.Length != 1)
            {
                return (null, "temporal filters require exactly one time-aware layer.");
            }

            var layer = ResolveLayer(service, layerIds[0]) ??
                await deps.LayerCatalog.GetLayerAsync(layerIds[0], cancellationToken).ConfigureAwait(false);
            if (layer is null)
            {
                return (null, $"Layer {layerIds[0]} not found.");
            }

            var timeInfo = layer.Metadata?.TimeInfo;
            if (timeInfo is null || string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
            {
                return (null, $"Layer {layer.Id} is not time-aware; temporal stream filters require layer timeInfo.");
            }

            if (!OgcTemporalFilterParser.TryParse(control.Datetime, layer, out var parsedTemporalFilter, out var temporalError) ||
                parsedTemporalFilter is null)
            {
                return (null, temporalError ?? "Invalid datetime parameter.");
            }

            temporalFilter = new StreamTemporalFilter(
                parsedTemporalFilter.Value.PropertyName,
                timeInfo.EndTimeField,
                parsedTemporalFilter.Value.Start,
                parsedTemporalFilter.Value.End);
        }

        return (new StreamSubscriptionFilter(serviceId, layerIds, bbox, attributeFilter, temporalFilter), null);
    }

    private static int[]? ResolveControlLayerIds(FeatureStreamControlMessage control)
    {
        if (control.LayerId.HasValue)
        {
            return [control.LayerId.Value];
        }

        if (control.Layers is { Length: > 0 })
        {
            return control.Layers;
        }

        if (control.LayerIds is { Length: > 0 })
        {
            return control.LayerIds;
        }

        return null;
    }

    private static async Task<(bool CloseRequested, string? Text, bool SizeExceeded)> ReceiveWebSocketTextAsync(
        WebSocket webSocket,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (true, null, false);
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return (false, null, false);
            }

            // Enforce the configured cap before allocating more memory: a
            // malicious client could otherwise buffer a never-ending fragmented
            // text frame and exhaust server memory on a streaming connection.
            if (stream.Length + result.Count > maxBytes)
            {
                return (false, null, true);
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (false, Encoding.UTF8.GetString(stream.ToArray()), false);
            }
        }
    }

    private static Task SendWebSocketStatusAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        FeatureStreamStatusFrame frame,
        CancellationToken cancellationToken)
        => SendWebSocketJsonAsync(
            webSocket,
            writeLock,
            JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamStatusFrame),
            cancellationToken);

    private static Task SendWebSocketErrorAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        string code,
        string message,
        string? subscriptionId,
        CancellationToken cancellationToken)
        => SendWebSocketJsonAsync(
            webSocket,
            writeLock,
            JsonSerializer.SerializeToUtf8Bytes(
                new FeatureStreamErrorFrame
                {
                    Code = code,
                    Message = message,
                    SubscriptionId = subscriptionId
                },
                FeatureStreamJsonContext.Default.FeatureStreamErrorFrame),
            cancellationToken);

    private static async Task SendWebSocketJsonAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        // The writer task drain, per-subscription replay, and control-handler frames
        // all share one socket. WebSocket.SendAsync is not safe to call concurrently
        // from multiple producers, so every send acquires the per-session write lock.
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task WriteSseEventAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        JsonTypeInfo<T> jsonTypeInfo,
        long? id,
        CancellationToken cancellationToken)
    {
        if (id.HasValue)
        {
            await response.WriteAsync(
                string.Concat("id: ", id.Value.ToString(CultureInfo.InvariantCulture), "\n"),
                cancellationToken).ConfigureAwait(false);
        }

        var json = JsonSerializer.Serialize(payload, jsonTypeInfo);
        await response.WriteAsync(
            string.Concat(
                "event: ", eventName, "\n",
                "data: ", json, "\n\n"),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleSseStream(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        FeatureStreamOptions options,
        ILogger logger,
        HttpContext context,
        IStreamSubscriptionFilter? subscriptionFilter)
    {
        var clientLabel = context.Request.Query["clientLabel"].ToString();
        var cursorParam = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorParam, CultureInfo.InvariantCulture, out var c) ? c : null;

        // Also check Last-Event-ID header (standard SSE reconnect mechanism).
        if (!cursor.HasValue)
        {
            var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
            if (long.TryParse(lastEventId, CultureInfo.InvariantCulture, out var lei))
            {
                cursor = lei;
            }
        }

        var session = sessionManager.TryCreateSession(SseTransport, NullIfEmpty(clientLabel), subscriptionFilter);
        if (session is null)
        {
            await WriteSessionLimitExceededAsync(context, options.MaxConcurrentSessions).ConfigureAwait(false);
            return;
        }

        using var sessionLease = session;

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        try
        {
            await WriteSseEventAsync(
                context.Response,
                "status",
                new FeatureStreamStatusFrame
                {
                    Status = "connected",
                    Message = "Feature stream connected.",
                    SessionId = session.SessionId
                },
                FeatureStreamJsonContext.Default.FeatureStreamStatusFrame,
                null,
                context.RequestAborted).ConfigureAwait(false);

            if (subscriptionFilter is not null)
            {
                await WriteSseEventAsync(
                    context.Response,
                    "status",
                    new FeatureStreamStatusFrame
                    {
                        Status = "subscribed",
                        Message = "Initial subscription accepted.",
                        SessionId = session.SessionId,
                        SubscriptionId = FeatureStreamSessionManager.DefaultSubscriptionId
                    },
                    FeatureStreamJsonContext.Default.FeatureStreamStatusFrame,
                    null,
                    context.RequestAborted).ConfigureAwait(false);
            }

            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // Client disconnected during handshake.
        }
        catch (IOException)
        {
            return; // Client disconnected during handshake.
        }
        catch (ObjectDisposedException)
        {
            return; // Client disconnected during handshake.
        }

        if (subscriptionFilter is not null)
        {
            FeatureStreamLog.SessionCreatedWithFilter(logger, session.SessionId, subscriptionFilter.Summary);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);

        // Replay missed events directly to the SSE response, bypassing the bounded channel
        // so that large replay backlogs are not truncated by the buffer limit.
        // Live broadcasts flow into the channel concurrently; the drain deduplicates
        // using the replay cursor so events are delivered exactly once.
        bool hasReplay = cursor.HasValue;
        long replayCursor = 0;
        if (hasReplay)
        {
            try
            {
                replayCursor = await ReplayToSseAsync(
                    context.Response,
                    eventStore,
                    cursor!.Value,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToSseAsync(
                    context.Response,
                    eventStore,
                    replayCursor,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                return; // Admin disconnect, slow-consumer removal, or request aborted during replay.
            }
            catch (IOException)
            {
                return; // Client disconnected during replay.
            }
            catch (ObjectDisposedException)
            {
                return; // Response stream disposed during replay.
            }
        }
        else
        {
            replayCursor = await eventStore.GetCurrentCursorAsync(linkedCts.Token).ConfigureAwait(false);
        }

        // Activate drain with buffer-sized grace for replay sessions so concurrent
        // overflows during the handoff are absorbed instead of disconnecting.
        sessionManager.MarkDrainStarted(session.SessionId,
            hasReplay ? options.MaxBufferPerConnection : 0);

        if (!hasReplay)
        {
            // Fresh live stream — no replay path, nothing to recover.
            sessionManager.ClearDrainGrace(session.SessionId);
        }

        // Convergent handoff: alternately drain the channel and sweep the store
        // until both are simultaneously empty.  Exits only when the channel is
        // empty AND the store has no new events, so ClearDrainGrace runs with
        // an empty channel and no unrecoverable grace-drops.
        try
        {
            if (hasReplay)
            {
                long previousCursor;
                do
                {
                    // Drain channel for headroom only — the store sweep below
                    // delivers everything in cursor order including grace-drops.
                    while (session.Reader.TryRead(out _)) { }

                    previousCursor = replayCursor;
                    replayCursor = await ReplayToSseAsync(
                        context.Response,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                } while (replayCursor > previousCursor || session.Reader.TryPeek(out _));
            }

            // For replay sessions, grace clear is deferred to the first drain
            // iteration (below) so the reader creates headroom for the final sweep.
            // For fresh sessions, grace was already cleared above.
            bool handoffDone = !hasReplay;

            // Deadline-based poll scheduling: nextPollAt advances by exactly one
            // interval per fire, regardless of how many channel-traffic loop
            // iterations elapsed in between. Without an absolute deadline,
            // continuous local broadcasts win the WhenAny race every iteration
            // and each round resets the poll delay to a fresh full interval,
            // starving the durable-store recovery path indefinitely.
            var nextPollAt = DateTimeOffset.UtcNow + options.CrossNodeSyncInterval;

            while (!linkedCts.Token.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                var waitToReadTask = session.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                var diff = nextPollAt - DateTimeOffset.UtcNow;
                var remainingDelay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
                var waitForPollTask = Task.Delay(remainingDelay, waitCts.Token);
                var completed = await Task.WhenAny(waitToReadTask, waitForPollTask).ConfigureAwait(false);

                if (completed == waitForPollTask)
                {
                    waitCts.Cancel();
                    try
                    {
                        await waitToReadTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
                    {
                        // Ignore expected cancellation for the alternate waiter.
                    }

                    replayCursor = await ReplayToSseAsync(
                        context.Response,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + options.CrossNodeSyncInterval;
                    continue;
                }

                waitCts.Cancel();
                if (!await waitToReadTask.ConfigureAwait(false))
                {
                    break;
                }

                try
                {
                    await waitForPollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
                {
                    // Ignore expected cancellation for the alternate waiter.
                }

                while (session.Reader.TryRead(out var message))
                {
                    if (!handoffDone)
                    {
                        bool progress;
                        do
                        {
                            progress = false;
                            while (session.Reader.TryRead(out _)) { }

                            long prev = replayCursor;
                            replayCursor = await ReplayToSseAsync(
                                context.Response,
                                eventStore,
                                replayCursor,
                                options.ReplayBatchSize,
                                logger,
                                session.SessionId,
                                linkedCts.Token,
                                subscriptionFilter,
                                FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                            if (replayCursor > prev)
                            {
                                progress = true;
                            }
                        } while (progress || session.Reader.TryPeek(out _));

                        sessionManager.ClearDrainGrace(session.SessionId);
                        handoffDone = true;
                        continue;
                    }

                    if (!message.IsHeartbeat && replayCursor > 0 && message.Envelope.Cursor <= replayCursor)
                    {
                        continue;
                    }

                    if (message.IsHeartbeat)
                    {
                        await WriteSseEventAsync(
                            context.Response,
                            "heartbeat",
                            new FeatureStreamHeartbeat(),
                            FeatureStreamJsonContext.Default.FeatureStreamHeartbeat,
                            null,
                            linkedCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteSseEventAsync(
                            context.Response,
                            "feature-change",
                            message.Envelope,
                            FeatureStreamJsonContext.Default.FeatureStreamEnvelope,
                            message.Envelope.Cursor,
                            linkedCts.Token).ConfigureAwait(false);

                        replayCursor = message.Envelope.Cursor;
                    }

                    await context.Response.Body.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                }

                // Drained the burst. If the poll deadline elapsed during the
                // drain, run the poll inline now — otherwise continuous broadcast
                // traffic could keep us pinned in the read branch and the
                // durable-store sweep would never fire.
                if (DateTimeOffset.UtcNow >= nextPollAt)
                {
                    replayCursor = await ReplayToSseAsync(
                        context.Response,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + options.CrossNodeSyncInterval;
                }
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // Client disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Response stream already disposed.
        }
    }

    private static async Task<long> ReplayToWebSocketAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken,
        IStreamSubscriptionFilter? subscriptionFilter = null,
        string? subscriptionId = null,
        FeatureStreamSessionManager? sessionManager = null,
        long subscriptionGeneration = 0)
    {
        var cursor = fromCursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt) with { SubscriptionId = subscriptionId };
                cursor = evt.Cursor;

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
                }

                // When a session manager and generation are supplied, claim the
                // (event, subscription) slot atomically. The claim also verifies
                // the subscription's generation, fencing stale replays after an
                // unsubscribe/replacement (although the per-connection control
                // loop is single-threaded, so this matches the writer-side
                // contract). Whichever send-time path wins the atomic test-and-
                // set sends the frame; the other observes the recorded key and
                // skips.
                if (sessionManager is not null && subscriptionId is not null && subscriptionGeneration > 0)
                {
                    if (sessionManager.TryClaimSubscriptionDelivery(sessionId, subscriptionId, subscriptionGeneration, evt.EventId)
                        != SubscriptionDeliveryClaim.Claimed)
                    {
                        continue;
                    }
                }
                else if (sessionManager is not null && subscriptionId is not null)
                {
                    // Generation-less call site (legacy/test); fall back to the dedup-only path.
                    if (!sessionManager.TryRememberSubscriptionDelivery(sessionId, subscriptionId, evt.EventId))
                    {
                        continue;
                    }
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, FeatureStreamJsonContext.Default.FeatureStreamEnvelope);
                await SendWebSocketJsonAsync(webSocket, writeLock, payload, cancellationToken).ConfigureAwait(false);
            }

            if (events.Count < batchSize)
            {
                break;
            }
        }

        return cursor;
    }

    private static async Task<long> ReplayToSseAsync(
        HttpResponse response,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken,
        IStreamSubscriptionFilter? subscriptionFilter = null,
        string? subscriptionId = null)
    {
        var cursor = fromCursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt) with { SubscriptionId = subscriptionId };
                cursor = evt.Cursor;

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
                }

                await WriteSseEventAsync(
                    response,
                    "feature-change",
                    envelope,
                    FeatureStreamJsonContext.Default.FeatureStreamEnvelope,
                    envelope.Cursor,
                    cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (events.Count < batchSize)
            {
                break;
            }
        }

        return cursor;
    }

    private static IResult HandleListSessions(
        [FromServices] FeatureStreamSessionManager sessionManager,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        var sessions = sessionManager.GetSessions();
        var now = DateTimeOffset.UtcNow;

        var sessionResponses = sessions.Select(s => new FeatureStreamSessionResponse
        {
            SessionId = s.SessionId,
            ConnectedAt = s.ConnectedAt,
            ClientLabel = s.ClientLabel,
            Transport = s.Transport,
            LastQueuedCursor = s.LastQueuedCursor,
            DurationSeconds = (now - s.ConnectedAt).TotalSeconds,
            HasFilter = s.HasFilter,
            FilterSummary = s.FilterSummary,
            ServiceIdFilter = s.ServiceIdFilter,
            LayerIdFilter = s.LayerIdFilter
        }).ToArray();

        var wsSessions = sessionResponses.Count(s => s.Transport == WebSocketTransport);
        var sseSessions = sessionResponses.Count(s => s.Transport == SseTransport);

        var response = new FeatureStreamStatusResponse
        {
            ActiveSessions = sessionResponses.Length,
            WebSocketSessions = wsSessions,
            SseSessions = sseSessions,
            SlowConsumerDrops = sessionManager.SlowConsumerDrops,
            HeartbeatsSent = sessionManager.HeartbeatsSent,
            Sessions = sessionResponses,
            GeneratedAt = now
        };

        return Results.Json(
            ApiResponse<FeatureStreamStatusResponse>.CreateSuccess(response),
            FeatureStreamJsonContext.Default.ApiResponseFeatureStreamStatusResponse);
    }

    private static IResult HandleDisconnectSession(
        Guid sessionId,
        [FromServices] FeatureStreamSessionManager sessionManager,
        HttpContext context,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        if (!sessionManager.DisconnectSession(sessionId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                string.Concat("Feature stream session ", sessionId.ToString(), " not found."));
        }

        return Results.Json(
            ApiResponse<object>.SuccessWithMessage(string.Concat("Session ", sessionId.ToString(), " disconnected.")),
            FeatureStreamJsonContext.Default.ApiResponseObject);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Task WriteSessionLimitExceededAsync(HttpContext context, int maxConcurrentSessions)
        => ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                $"Feature stream session limit of {maxConcurrentSessions} concurrent sessions reached.")
            .ExecuteAsync(context);
}

/// <summary>
/// Log category for feature stream endpoints.
/// </summary>
internal sealed class FeatureStreamEndpointsLog;
