// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
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
            [FromServices] IServiceMetadataUpdater metadataUpdater,
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

            var metadata = (service.Metadata ?? new CatalogMetadata()) with
            {
                EnabledProtocols = normalizedProtocols
            };

            await metadataUpdater.UpdateServiceMetadataAsync(serviceName, metadata, context.RequestAborted);
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

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateMapServerSettings(
            string serviceName,
            UpdateMapServerSettingsRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] IServiceMetadataUpdater metadataUpdater,
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

            var existing = service.Metadata?.MapServer ?? new MapServerConfig();
            var updated = new MapServerConfig
            {
                MaxImageWidth = request.MaxImageWidth ?? existing.MaxImageWidth,
                MaxImageHeight = request.MaxImageHeight ?? existing.MaxImageHeight,
                DefaultImageWidth = request.DefaultImageWidth ?? existing.DefaultImageWidth,
                DefaultImageHeight = request.DefaultImageHeight ?? existing.DefaultImageHeight,
                DefaultDpi = request.DefaultDpi ?? existing.DefaultDpi,
                DefaultFormat = request.DefaultFormat ?? existing.DefaultFormat,
                DefaultTransparent = request.DefaultTransparent ?? existing.DefaultTransparent,
                MaxFeaturesPerLayer = request.MaxFeaturesPerLayer ?? existing.MaxFeaturesPerLayer
            };

            var metadata = (service.Metadata ?? new CatalogMetadata()) with
            {
                MapServer = updated
            };

            await metadataUpdater.UpdateServiceMetadataAsync(serviceName, metadata, context.RequestAborted);
            await InvalidateServiceCatalogCacheAsync(context, serviceName, service, logger).ConfigureAwait(false);

            // Re-read to return updated state
            var refreshed = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            var response = BuildSettingsResponse(refreshed!);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateMapServerSettingsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "MapServer settings update failed",
                detail: "An internal error occurred while updating MapServer settings.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateAccessPolicy(
            string serviceName,
            UpdateAccessPolicyRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] IServiceMetadataUpdater metadataUpdater,
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

            var existing = service.Metadata?.AccessPolicy ?? new AccessPolicy();
            var updated = existing with
            {
                AllowAnonymous = request.AllowAnonymous ?? existing.AllowAnonymous,
                AllowAnonymousWrite = request.AllowAnonymousWrite ?? existing.AllowAnonymousWrite,
                AllowedRoles = request.AllowedRoles ?? existing.AllowedRoles,
                AllowedWriteRoles = request.AllowedWriteRoles ?? existing.AllowedWriteRoles
            };

            var metadata = (service.Metadata ?? new CatalogMetadata()) with
            {
                AccessPolicy = updated
            };

            await metadataUpdater.UpdateServiceMetadataAsync(serviceName, metadata, context.RequestAborted);
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

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateTimeInfo(
            string serviceName,
            UpdateTimeInfoRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] IServiceMetadataUpdater metadataUpdater,
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

            var existing = service.Metadata?.TimeInfo ?? new LayerTimeInfo();
            var updated = existing with
            {
                StartTimeField = NormalizeEmptyToNull(request.StartTimeField) ?? existing.StartTimeField,
                EndTimeField = NormalizeEmptyToNull(request.EndTimeField) ?? existing.EndTimeField,
                TrackIdField = NormalizeEmptyToNull(request.TrackIdField) ?? existing.TrackIdField
            };

            // If all fields were explicitly set to empty string, clear the entire time info
            var clearStart = request.StartTimeField is "";
            var clearEnd = request.EndTimeField is "";
            var clearTrack = request.TrackIdField is "";

            if (clearStart)
            {
                updated = updated with { StartTimeField = null };
            }

            if (clearEnd)
            {
                updated = updated with { EndTimeField = null };
            }

            if (clearTrack)
            {
                updated = updated with { TrackIdField = null };
            }

            var metadata = (service.Metadata ?? new CatalogMetadata()) with
            {
                TimeInfo = updated
            };

            await metadataUpdater.UpdateServiceMetadataAsync(serviceName, metadata, context.RequestAborted);
            await InvalidateServiceCatalogCacheAsync(context, serviceName, service, logger).ConfigureAwait(false);

            var refreshed = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            var response = BuildSettingsResponse(refreshed!);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateTimeInfoFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Time info update failed",
                detail: "An internal error occurred while updating time info.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<LayerMetadataResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateLayerMetadata(
            string serviceName,
            int layerId,
            UpdateLayerMetadataRequest request,
            [FromServices] ILayerCatalog catalog,
            [FromServices] ILayerMetadataUpdater layerMetadataUpdater,
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

            var metadata = existingMetadata with
            {
                AccessPolicy = updatedAccessPolicy,
                TimeInfo = updatedTimeInfo
            };

            await layerMetadataUpdater.UpdateLayerMetadataAsync(layerId, metadata, context.RequestAborted);
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
            } : null
        };
    }

    private static string? NormalizeEmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

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
}
