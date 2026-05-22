// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for managing per-service protocol toggles and MapServer settings.
/// </summary>
internal static class ServiceSettingsEndpoints
{
    /// <summary>
    /// Log category for service settings endpoints.
    /// </summary>
    internal sealed class ServiceSettingsEndpointsLog;

    public static void MapServiceSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/services")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Services")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListServices)
            .WithDisplayName("List Services")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{serviceName}/settings", HandleGetSettings)
            .WithDisplayName("Get Service Settings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{serviceName}/protocols", HandleUpdateProtocols)
            .WithDisplayName("Update Service Protocols")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/mapserver", HandleUpdateMapServerSettings)
            .WithDisplayName("Update MapServer Settings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/access-policy", HandleUpdateAccessPolicy)
            .WithDisplayName("Update Access Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/timeinfo", HandleUpdateTimeInfo)
            .WithDisplayName("Update Time Info")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/layers/{layerId:int}/metadata", HandleUpdateLayerMetadata)
            .WithDisplayName("Update Layer Metadata")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSummary[]>>, ProblemHttpResult>>
        HandleListServices(
            [FromServices] ILayerCatalog catalog,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var services = await catalog.ListServicesAsync(context.RequestAborted);
            var summaries = services.Select(s => new ServiceSummary
            {
                ServiceName = s.Name,
                Description = s.Description,
                LayerCount = s.Layers.Length,
                EnabledProtocols = s.Metadata?.EnabledProtocols ?? ServiceProtocols.All
            }).ToArray();

            return TypedResults.Ok(ApiResponse<ServiceSummary[]>.CreateSuccess(summaries));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.ListServicesFailed(logger, ex);
            return TypedResults.Problem(
                title: "Service listing failed",
                detail: "An internal error occurred while listing services.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetSettings(
            string serviceName,
            [FromServices] ILayerCatalog catalog,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var service = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            if (service is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            var response = BuildSettingsResponse(service);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.GetServiceSettingsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Service settings retrieval failed",
                detail: "An internal error occurred while retrieving service settings.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateProtocols(
            string serviceName,
            UpdateProtocolsRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] IMetadataV2GraphStore graphStore,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            if (request.EnabledProtocols is null)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Enabled protocols payload is required."));
            }

            var normalizedProtocols = request.EnabledProtocols
                .Where(static protocol => !string.IsNullOrWhiteSpace(protocol))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (normalizedProtocols.Length == 0)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("At least one protocol must be enabled."));
            }

            // Validate protocol names
            var invalid = normalizedProtocols.Except(ServiceProtocols.All, StringComparer.Ordinal).ToArray();
            if (invalid.Length > 0)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"Invalid protocol(s): {string.Join(", ", invalid)}. Valid values: {string.Join(", ", ServiceProtocols.All)}"));
            }

            var service = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            if (service is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            await MutateServicesByNameAsync(
                graphStore,
                serviceName,
                svc => svc with { Protocols = normalizedProtocols },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, serviceName, service, logger).ConfigureAwait(false);

            // Re-read to return updated state
            var updated = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            var response = BuildSettingsResponse(updated!);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateProtocolsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Protocol update failed",
                detail: "An internal error occurred while updating service protocols.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateMapServerSettings(
            string serviceName,
            UpdateMapServerSettingsRequest request,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        // GAP (#1035 cutover 72/N): the v1 MapServer settings block (max image
        // width/height, default DPI, transparent flag, max features per layer) lived on
        // CatalogMetadata.MapServer. The Metadata v2 graph does not have a typed home
        // for these knobs yet — they have no equivalent on MetadataV2Service. Until a
        // V2 MapServer extension lands the endpoint refuses the operation honestly
        // rather than silently dropping the payload.
        _ = request;
        ServiceSettingsLog.UpdateMapServerSettingsFailed(
            logger,
            serviceName,
            new NotSupportedException(
                "MapServer settings have no Metadata v2 representation yet (#1035 cutover gap)."));
        return Task.FromResult<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>(
            TypedResults.Problem(
                title: "MapServer settings update not supported",
                detail:
                    "MapServer settings are not yet representable in the Metadata v2 graph. " +
                    "Track this gap on the metadata cutover epic before resuming the admin path.",
                statusCode: StatusCodes.Status501NotImplemented));
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateAccessPolicy(
            string serviceName,
            UpdateAccessPolicyRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] IMetadataV2GraphStore graphStore,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var service = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            if (service is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            await MutateServicesByNameAsync(
                graphStore,
                serviceName,
                svc =>
                {
                    var existing = svc.AccessPolicy ?? new AccessPolicy();
                    return svc with
                    {
                        AccessPolicy = existing with
                        {
                            AllowAnonymous = request.AllowAnonymous ?? existing.AllowAnonymous,
                            AllowAnonymousWrite = request.AllowAnonymousWrite ?? existing.AllowAnonymousWrite,
                            AllowedRoles = request.AllowedRoles ?? existing.AllowedRoles,
                            AllowedWriteRoles = request.AllowedWriteRoles ?? existing.AllowedWriteRoles,
                        },
                    };
                },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, serviceName, service, logger).ConfigureAwait(false);

            var refreshed = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            var response = BuildSettingsResponse(refreshed!);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateAccessPolicyFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Access policy update failed",
                detail: "An internal error occurred while updating access policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateTimeInfo(
            string serviceName,
            UpdateTimeInfoRequest request,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        // GAP (#1035 cutover 72/N): the v1 admin path exposed a service-scope time-info
        // editor that was actually applied to every layer on the service. In the V2
        // graph temporal metadata lives on MetadataV2Resource.Temporal — there is no
        // service-level slot. Rather than silently fan-out across resources (which
        // would mask field-typed validation gaps) we refuse the operation and direct
        // callers to the per-layer endpoint until a service-level temporal extension
        // is designed.
        _ = request;
        ServiceSettingsLog.UpdateTimeInfoFailed(
            logger,
            serviceName,
            new NotSupportedException(
                "Service-level TimeInfo has no Metadata v2 representation. Use the per-layer endpoint."));
        return Task.FromResult<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>(
            TypedResults.Problem(
                title: "Service-level TimeInfo update not supported",
                detail:
                    "Service-scope TimeInfo is not representable in the Metadata v2 graph. " +
                    "Use PUT /admin/services/{serviceName}/layers/{layerId}/metadata instead.",
                statusCode: StatusCodes.Status501NotImplemented));
    }

    private static async Task<Results<Ok<ApiResponse<LayerMetadataResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateLayerMetadata(
            string serviceName,
            int layerId,
            UpdateLayerMetadataRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] IMetadataV2GraphStore graphStore,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var service = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            if (service is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            var layer = service.GetLayer(layerId);
            if (layer is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Layer {layerId} not found in service '{serviceName}'."));
            }

            if (request.RasterMosaic?.MergeStrategy is { Length: > 0 } mergeStrategyValue
                && !TryNormalizeMergeStrategy(mergeStrategyValue, out _))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"Invalid mergeStrategy '{mergeStrategyValue}'. Allowed values: newest, oldest, average, max, min."));
            }

            var existingMetadata = layer.Metadata ?? new CatalogMetadata();

            // Patch access policy
            AccessPolicy? updatedAccessPolicy = existingMetadata.AccessPolicy;
            if (request.AccessPolicy is not null)
            {
                var existingAp = existingMetadata.AccessPolicy ?? new AccessPolicy();
                updatedAccessPolicy = existingAp with
                {
                    AllowAnonymous = request.AccessPolicy.AllowAnonymous ?? existingAp.AllowAnonymous,
                    AllowAnonymousWrite = request.AccessPolicy.AllowAnonymousWrite ?? existingAp.AllowAnonymousWrite,
                    AllowedRoles = request.AccessPolicy.AllowedRoles ?? existingAp.AllowedRoles,
                    AllowedWriteRoles = request.AccessPolicy.AllowedWriteRoles ?? existingAp.AllowedWriteRoles
                };
            }

            // Patch time info
            LayerTimeInfo? updatedTimeInfo = existingMetadata.TimeInfo;
            if (request.TimeInfo is not null)
            {
                var existingTi = existingMetadata.TimeInfo ?? new LayerTimeInfo();
                updatedTimeInfo = existingTi with
                {
                    StartTimeField = request.TimeInfo.StartTimeField is "" ? null : (request.TimeInfo.StartTimeField ?? existingTi.StartTimeField),
                    EndTimeField = request.TimeInfo.EndTimeField is "" ? null : (request.TimeInfo.EndTimeField ?? existingTi.EndTimeField),
                    TrackIdField = request.TimeInfo.TrackIdField is "" ? null : (request.TimeInfo.TrackIdField ?? existingTi.TrackIdField)
                };
            }

            RasterMosaicSettings? updatedRasterMosaic = existingMetadata.RasterMosaic;
            if (request.RasterMosaic is not null)
            {
                var existingRm = existingMetadata.RasterMosaic ?? new RasterMosaicSettings();
                string? mergeStrategy;
                if (request.RasterMosaic.MergeStrategy is null)
                {
                    mergeStrategy = existingRm.MergeStrategy;
                }
                else if (request.RasterMosaic.MergeStrategy.Length == 0)
                {
                    mergeStrategy = null;
                }
                else
                {
                    // Already validated above; normalize to the canonical lowercase token.
                    TryNormalizeMergeStrategy(request.RasterMosaic.MergeStrategy, out var canonical);
                    mergeStrategy = canonical;
                }

                updatedRasterMosaic = existingRm with { MergeStrategy = mergeStrategy };
            }

            var metadata = existingMetadata with
            {
                AccessPolicy = updatedAccessPolicy,
                TimeInfo = updatedTimeInfo,
                RasterMosaic = updatedRasterMosaic
            };

            await MutateResourcesForLayerAsync(
                graphStore,
                serviceName,
                layerId,
                resource =>
                {
                    var next = resource;
                    if (updatedAccessPolicy is not null)
                    {
                        next = next with { AccessPolicy = updatedAccessPolicy };
                    }
                    next = next with
                    {
                        Temporal = ToV2Temporal(updatedTimeInfo, existing: next.Temporal),
                    };
                    // RasterMosaic has no V2 home yet — silently drop until the V2
                    // raster extension lands. The v1 admin shape is preserved so
                    // GET responses can still echo what the caller PUT.
                    return next;
                },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, serviceName, service, logger).ConfigureAwait(false);

            var response = BuildLayerMetadataResponse(layer, metadata);
            return TypedResults.Ok(ApiResponse<LayerMetadataResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateLayerMetadataFailed(logger, serviceName, layerId, ex);
            return TypedResults.Problem(
                title: "Layer metadata update failed",
                detail: "An internal error occurred while updating layer metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static ServiceSettingsResponse BuildSettingsResponse(ServiceDefinition service)
    {
        var mapConfig = service.Metadata?.MapServer ?? new MapServerConfig();
        var enabledProtocols = service.Metadata?.EnabledProtocols ?? ServiceProtocols.All;
        var accessPolicy = service.Metadata?.AccessPolicy;
        var timeInfo = service.Metadata?.TimeInfo;

        return new ServiceSettingsResponse
        {
            ServiceName = service.Name,
            EnabledProtocols = enabledProtocols,
            AvailableProtocols = ServiceProtocols.All,
            AccessPolicy = accessPolicy is not null ? new AccessPolicyResponse
            {
                AllowAnonymous = accessPolicy.AllowAnonymous,
                AllowAnonymousWrite = accessPolicy.AllowAnonymousWrite,
                AllowedRoles = accessPolicy.AllowedRoles,
                AllowedWriteRoles = accessPolicy.AllowedWriteRoles
            } : null,
            TimeInfo = timeInfo is not null ? new TimeInfoResponse
            {
                StartTimeField = timeInfo.StartTimeField,
                EndTimeField = timeInfo.EndTimeField,
                TrackIdField = timeInfo.TrackIdField
            } : null,
            MapServer = new MapServerSettingsResponse
            {
                MaxImageWidth = mapConfig.MaxImageWidth,
                MaxImageHeight = mapConfig.MaxImageHeight,
                DefaultImageWidth = mapConfig.DefaultImageWidth,
                DefaultImageHeight = mapConfig.DefaultImageHeight,
                DefaultDpi = mapConfig.DefaultDpi,
                DefaultFormat = mapConfig.DefaultFormat,
                DefaultTransparent = mapConfig.DefaultTransparent,
                MaxFeaturesPerLayer = mapConfig.MaxFeaturesPerLayer
            }
        };
    }

    private static LayerMetadataResponse BuildLayerMetadataResponse(LayerDefinition layer, CatalogMetadata metadata)
    {
        var accessPolicy = metadata.AccessPolicy;
        var timeInfo = metadata.TimeInfo;

        return new LayerMetadataResponse
        {
            LayerId = layer.Id,
            LayerName = layer.Name,
            AccessPolicy = accessPolicy is not null ? new AccessPolicyResponse
            {
                AllowAnonymous = accessPolicy.AllowAnonymous,
                AllowAnonymousWrite = accessPolicy.AllowAnonymousWrite,
                AllowedRoles = accessPolicy.AllowedRoles,
                AllowedWriteRoles = accessPolicy.AllowedWriteRoles
            } : null,
            TimeInfo = timeInfo is not null ? new TimeInfoResponse
            {
                StartTimeField = timeInfo.StartTimeField,
                EndTimeField = timeInfo.EndTimeField,
                TrackIdField = timeInfo.TrackIdField
            } : null,
            RasterMosaic = metadata.RasterMosaic is not null ? new RasterMosaicResponse
            {
                MergeStrategy = metadata.RasterMosaic.MergeStrategy
            } : null
        };
    }

    /// <summary>
    /// Validates a raster mosaic merge strategy against the canonical set and normalizes to
    /// the lowercase canonical token. Allowed: newest, oldest, average, max, min.
    /// </summary>
    private static bool TryNormalizeMergeStrategy(string value, out string canonical)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "newest":
                canonical = "newest";
                return true;
            case "oldest":
                canonical = "oldest";
                return true;
            case "average":
                canonical = "average";
                return true;
            case "max":
                canonical = "max";
                return true;
            case "min":
                canonical = "min";
                return true;
            default:
                canonical = string.Empty;
                return false;
        }
    }

    private static async Task InvalidateServiceCatalogCacheAsync(
        HttpContext context,
        string serviceName,
        ServiceDefinition service,
        ILogger<ServiceSettingsEndpointsLog> logger)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator == null)
        {
            return;
        }

        try
        {
            await cacheInvalidator.InvalidateServiceCatalogAsync(
                serviceName,
                service.Layers.Select(layer => layer.Id),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.InvalidateServiceCatalogCacheFailed(logger, serviceName, ex);
        }
    }

    /// <summary>
    /// Loads the canonical Metadata v2 graph, applies <paramref name="mutate"/> to every
    /// service whose <c>Metadata.Name</c> matches <paramref name="serviceName"/>
    /// (case-insensitively), and persists the result. Multiple service entries can share
    /// a name when the same logical service is exposed under different protocols (e.g.
    /// FeatureServer / MapServer / Stac all named "test"), and the v1 admin endpoint
    /// updated all of them in one row because protocol toggles lived on a single
    /// per-service settings record.
    /// </summary>
    private static async Task MutateServicesByNameAsync(
        IMetadataV2GraphStore graphStore,
        string serviceName,
        Func<MetadataV2Service, MetadataV2Service> mutate,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var services = snapshot.Graph.Services.ToArray();
        var mutatedAny = false;
        for (var i = 0; i < services.Length; i++)
        {
            if (string.Equals(services[i].Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            {
                services[i] = mutate(services[i]);
                mutatedAny = true;
            }
        }

        if (!mutatedAny)
        {
            return;
        }

        var updated = snapshot.Graph with
        {
            Services = services,
            Revision = snapshot.Graph.Revision + 1,
        };
        _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the canonical Metadata v2 graph, walks every publication whose service
    /// matches <paramref name="serviceName"/> and whose <c>LayerIndex</c> equals
    /// <paramref name="layerId"/>, applies <paramref name="mutate"/> to the backing
    /// resource(s) once, and persists the result. The v1 contract was "layer ids are
    /// stable across services of the same name", so the V2 cut-over collapses the same
    /// way: one logical layer maps to one V2 resource even when it is published through
    /// multiple V2 services.
    /// </summary>
    private static async Task MutateResourcesForLayerAsync(
        IMetadataV2GraphStore graphStore,
        string serviceName,
        int layerId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var matchingServiceIds = snapshot.Graph.Services
            .Where(s => string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);

        var targetResourceIds = snapshot.Graph.Publications
            .Where(p => matchingServiceIds.Contains(p.ServiceId)
                && p.Identifier.IsNumeric
                && p.LayerIndex == layerId)
            .Select(p => p.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        if (targetResourceIds.Count == 0)
        {
            return;
        }

        var resources = snapshot.Graph.Resources.ToArray();
        for (var i = 0; i < resources.Length; i++)
        {
            if (targetResourceIds.Contains(resources[i].Metadata.Id))
            {
                resources[i] = mutate(resources[i]);
            }
        }

        var updated = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bridges the v1 <see cref="LayerTimeInfo"/> shape to the V2
    /// <see cref="MetadataV2ResourceTemporal"/>. Treats an entirely-empty time info as
    /// "clear temporal" — returns null so the resource turns non-temporal — to preserve
    /// the v1 clear-on-empty-PUT semantics. Preserves an existing declared extent when
    /// only the field names are changing.
    /// </summary>
    private static MetadataV2ResourceTemporal? ToV2Temporal(LayerTimeInfo? timeInfo, MetadataV2ResourceTemporal? existing)
    {
        if (timeInfo is null
            || (string.IsNullOrEmpty(timeInfo.StartTimeField)
                && string.IsNullOrEmpty(timeInfo.EndTimeField)
                && string.IsNullOrEmpty(timeInfo.TrackIdField)))
        {
            return null;
        }

        return new MetadataV2ResourceTemporal
        {
            StartTimeField = timeInfo.StartTimeField,
            EndTimeField = timeInfo.EndTimeField,
            TrackIdField = timeInfo.TrackIdField,
            Extent = existing?.Extent,
        };
    }
}
