// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
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
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSummary[]>>, BadRequest<ApiResponse<object>>>>
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
                Description = s.Description
            }).ToArray();

            return TypedResults.Ok(ApiResponse<ServiceSummary[]>.CreateSuccess(summaries));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list services");
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to list services."));
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>>
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
            logger.LogWarning(ex, "Failed to get service settings for {ServiceName}", serviceName);
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to get service settings."));
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>>
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
            // Validate protocol names
            var invalid = request.EnabledProtocols.Except(ServiceProtocols.All).ToArray();
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
                EnabledProtocols = request.EnabledProtocols
            };

            await metadataUpdater.UpdateServiceMetadataAsync(serviceName, metadata, context.RequestAborted);

            // Re-read to return updated state
            var updated = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            var response = BuildSettingsResponse(updated!);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update protocols for {ServiceName}", serviceName);
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to update service protocols."));
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>>>
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

            // Re-read to return updated state
            var refreshed = await catalog.GetServiceAsync(serviceName, context.RequestAborted);
            var response = BuildSettingsResponse(refreshed!);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update MapServer settings for {ServiceName}", serviceName);
            return TypedResults.BadRequest(ApiResponse<object>.Failure("Failed to update MapServer settings."));
        }
    }

    private static ServiceSettingsResponse BuildSettingsResponse(ServiceDefinition service)
    {
        var mapConfig = service.Metadata?.MapServer ?? new MapServerConfig();

        return new ServiceSettingsResponse
        {
            ServiceName = service.Name,
            EnabledProtocols = service.Metadata?.EnabledProtocols ?? ServiceProtocols.All,
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
}
