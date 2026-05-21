// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Subscription filter parsing and validation for feature-stream query parameters
/// (layers, bbox, attribute filter, temporal datetime) plus the shared layer
/// authorization helpers used by both query-string and WebSocket control paths.
/// </summary>
internal static partial class FeatureStreamEndpoints
{
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
}
