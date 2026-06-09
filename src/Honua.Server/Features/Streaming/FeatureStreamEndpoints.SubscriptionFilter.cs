// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Common;

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

        var snapshot = await deps.MetadataV2GraphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        MetadataV2Service? service = null;

        if (serviceId is not null)
        {
            service = ResolveStreamService(snapshot, serviceId);
            if (service is null)
            {
                var msg = $"Service '{serviceId}' not found.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            serviceId = service.Metadata.Name;
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
            var (parsedIds, layerError) = ParseAndAuthorizeLayerIds(
                context,
                logger,
                service,
                snapshot,
                layerSource);
            if (layerError is not null)
            {
                return (null, false, layerError);
            }

            layerIds = parsedIds;
            hasAnyFilter = true;
        }

        if (service is not null && layerIds is null)
        {
            var accessError = RequireAllLayerAccess(context, snapshot, service);
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

            var bboxLayer = ResolveStreamLayer(snapshot, service, layerIds[0]);
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

                var filterLayer = ResolveStreamLayer(snapshot, service, layerIds[0]);
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

            var temporalLayer = ResolveStreamLayer(snapshot, service, layerIds[0]);
            if (temporalLayer is null)
            {
                var msg = $"Layer {layerIds[0]} not found.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (!TemporalExtentHelpers.HasOptInTemporalFields(temporalLayer.Resource))
            {
                var msg = $"Layer {layerIds[0]} is not time-aware; temporal stream filters require resource temporal metadata.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            if (!TryBuildStreamTemporalFilter(datetimeParam, temporalLayer.Resource, out var parsedTemporalFilter, out var temporalError) ||
                parsedTemporalFilter is null)
            {
                var msg = temporalError ?? "Invalid datetime parameter.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, false, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            temporalFilter = parsedTemporalFilter;
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

    private static bool TryBuildStreamTemporalFilter(
        string? datetime,
        MetadataV2Resource resource,
        out StreamTemporalFilter? temporalFilter,
        out string? errorMessage)
    {
        temporalFilter = null;

        if (!OgcTemporalFilterParser.TryParseRange(datetime, out var start, out var end, out errorMessage))
        {
            return false;
        }

        if (start is null && end is null)
        {
            return true;
        }

        if (!TemporalExtentHelpers.TryResolveOptInTemporalFieldsV2(resource, out var fields))
        {
            errorMessage = "No temporal field is available for filtering.";
            return false;
        }

        temporalFilter = new StreamTemporalFilter(
            fields.StartFieldName,
            fields.EndFieldName,
            start,
            end);
        return true;
    }

    private static (int[]? Ids, IResult? Error) ParseAndAuthorizeLayerIds(
        HttpContext context,
        ILogger logger,
        MetadataV2Service? service,
        MetadataV2GraphSnapshot snapshot,
        string layersValue)
    {
        var ids = new List<int>();
        foreach (var part in layersValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, CultureInfo.InvariantCulture, out var id))
            {
                var msg = $"Invalid layer ID '{part}'. Must be an integer.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
            }

            ids.Add(id);
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

        var resolvedIds = new List<int>(ids.Count);
        var seenResolved = new HashSet<int>();
        foreach (var id in ids)
        {
            // When a serviceId was provided, restrict layer ids to that service so a
            // caller cannot piggy-back unrelated layers on an authorized service.
            // When no serviceId was provided, look up the layer's primary service so
            // the access policy check evaluates the service-level policy too.
            StreamLayerDescriptor? layer;
            MetadataV2Service? authService;
            if (service is not null)
            {
                layer = ResolveStreamLayer(snapshot, service, id);
                if (layer is null)
                {
                    var msg = $"Layer {id} is not part of service '{service.Metadata.Name}'.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                authService = service;
            }
            else
            {
                layer = ResolveStreamLayer(snapshot, service: null, id);
                if (layer is null)
                {
                    var msg = $"Layer {id} not found.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, StandardErrorHelpers.CreateBadRequest(context, msg));
                }

                authService = layer.Service;
            }

            var accessError = RequireStreamLayerAccess(context, layer.Resource, authService);
            if (accessError is not null)
            {
                return (null, accessError);
            }

            if (seenResolved.Add(layer.LayerId))
            {
                resolvedIds.Add(layer.LayerId);
            }
        }

        return (resolvedIds.ToArray(), null);
    }

    private static IResult? RequireAllLayerAccess(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        foreach (var layer in ResolveServiceStreamLayers(snapshot, service))
        {
            var accessError = RequireStreamLayerAccess(context, layer.Resource, service);
            if (accessError is not null)
            {
                return accessError;
            }
        }

        return null;
    }

    private static IResult? RequireStreamLayerAccess(
        HttpContext context,
        MetadataV2Resource resource,
        MetadataV2Service? service)
        => AccessPolicyHelpers.RequireResourceAccess(context, resource, service);

    private static MetadataV2Service? ResolveStreamService(
        MetadataV2GraphSnapshot snapshot,
        string serviceId)
    {
        if (snapshot.Index.ServicesByName.TryGetValue(serviceId, out var byName))
        {
            return byName;
        }

        if (snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId))
        {
            return byId;
        }

        return snapshot.Graph.Services.FirstOrDefault(candidate =>
            string.Equals(candidate.Metadata.Id, serviceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Metadata.Name, serviceId, StringComparison.OrdinalIgnoreCase));
    }

    private static StreamLayerDescriptor? ResolveStreamLayer(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service? service,
        int layerId)
    {
        var layers = service is null
            ? ResolveAllStreamLayers(snapshot)
            : ResolveServiceStreamLayers(snapshot, service);

        foreach (var layer in layers
            .Where(layer => layer.LayerId == layerId || layer.PublicLayerId == layerId)
            .OrderByDescending(layer => layer.LayerId == layerId)
            .ThenByDescending(layer => layer.Publication?.IsPrimary ?? false)
            .ThenBy(layer => layer.Service?.Metadata.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return layer;
        }

        return null;
    }

    private static IEnumerable<StreamLayerDescriptor> ResolveServiceStreamLayers(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service)
    {
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            var layer = CreateStreamLayer(snapshot, publication, service);
            if (layer is not null)
            {
                yield return layer;
            }
        }
    }

    private static IEnumerable<StreamLayerDescriptor> ResolveAllStreamLayers(MetadataV2GraphSnapshot snapshot)
    {
        foreach (var binding in snapshot.Index.StorageBindingsByStorageLayerId.Values)
        {
            if (!binding.StorageLayerId.HasValue ||
                !snapshot.Index.ResourcesByStorageLayerId.TryGetValue(binding.StorageLayerId.Value, out var resource))
            {
                continue;
            }

            var publication = ResolvePrimaryStreamPublication(snapshot, resource);
            var service = publication is not null &&
                snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var resolvedService)
                    ? resolvedService
                    : null;

            yield return new StreamLayerDescriptor(
                resource,
                service,
                publication,
                binding.StorageLayerId.Value,
                publication?.LayerIndex);
        }
    }

    private static StreamLayerDescriptor? CreateStreamLayer(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Service? service)
    {
        var resource = snapshot.ResolveResource(publication);
        if (resource is null)
        {
            return null;
        }

        var storageLayerId = snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource)
            ?? publication.LayerIndex;
        if (!storageLayerId.HasValue)
        {
            return null;
        }

        return new StreamLayerDescriptor(
            resource,
            service,
            publication,
            storageLayerId.Value,
            publication.LayerIndex);
    }

    private static MetadataV2Publication? ResolvePrimaryStreamPublication(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Resource resource)
        => snapshot.Graph.Publications
            .Where(publication => string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
            .OrderByDescending(static publication => publication.IsPrimary)
            .ThenBy(publication =>
                snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service)
                    ? service.Metadata.Name
                    : publication.ServiceId,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private sealed record StreamLayerDescriptor(
        MetadataV2Resource Resource,
        MetadataV2Service? Service,
        MetadataV2Publication? Publication,
        int LayerId,
        int? PublicLayerId);

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
        StreamLayerDescriptor layer,
        out string error)
    {
        var schema = FilterFieldSchema.From(layer.Resource);

        foreach (var property in EnumeratePropertyReferences(expression))
        {
            if (!schema.TryGetFieldType(property.PropertyName, out _))
            {
                error = $"Unknown field '{property.PropertyName}' in streaming filter for layer {layer.LayerId}.";
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
        StreamLayerDescriptor layer,
        CancellationToken cancellationToken)
    {
        if (layer.Resource.ReadGeometryType() is MetadataV2GeometryType.None)
        {
            return (bbox, $"bbox filters are not supported for non-spatial layer {layer.LayerId}.");
        }

        var layerSrid = layer.Resource.ReadSrid();
        if (!layerSrid.HasValue || layerSrid.Value <= 0)
        {
            return (bbox, $"Layer {layer.LayerId} does not define a valid spatial reference.");
        }

        if (layerSrid.Value == SubscriptionBboxSrid)
        {
            return (bbox, null);
        }

        try
        {
            var projectedWkb = await deps.GeometryOperationService.ProjectAsync(
                SpatialFilterHelpers.CreateEnvelopeWkb(bbox[0], bbox[1], bbox[2], bbox[3]),
                SubscriptionBboxSrid,
                layerSrid.Value,
                cancellationToken).ConfigureAwait(false);

            var geometry = WkbReaderCache.Get().Read(projectedWkb);
            if (geometry is null || geometry.IsEmpty)
            {
                return (bbox, $"Unable to project bbox to layer {layer.LayerId} spatial reference.");
            }

            var env = geometry.EnvelopeInternal;
            return ([env.MinX, env.MinY, env.MaxX, env.MaxY], null);
        }
        catch (ArgumentException)
        {
            return (bbox, $"Invalid bbox projection for layer {layer.LayerId}.");
        }
        catch (NotSupportedException)
        {
            return (bbox, $"bbox filters do not support projecting layer {layer.LayerId} to SRID {layerSrid.Value}.");
        }
        catch (InvalidOperationException)
        {
            return (bbox, $"bbox filters could not be projected for layer {layer.LayerId}.");
        }
    }
}
