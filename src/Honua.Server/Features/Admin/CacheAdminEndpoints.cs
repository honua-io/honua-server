// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for cache health monitoring and invalidation.
/// </summary>
internal static class CacheAdminEndpoints
{
    private const string CacheInvalidationFailedMessage = "Cache invalidation failed.";
    internal sealed class CacheAdminEndpointsLog;

    public static void MapCacheAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/cache")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Cache")
            .RequireAdminAuthorization();

        group.MapGet("/status", HandleGetCacheStatus)
            .WithDisplayName("Get Cache Status")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<CacheStatusResponse>>();

        group.MapPost("/invalidate", HandleInvalidateCache)
            .WithDisplayName("Invalidate Cache")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<CacheInvalidationResponse>>();
    }

    private static async Task<IResult> HandleGetCacheStatus(
        [FromServices] ICacheHealthChecker cacheHealthChecker,
        [FromServices] ILogger<CacheAdminEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        AdminLog.CacheStatusQueried(logger);

        var isHealthy = await cacheHealthChecker.IsCacheHealthyAsync(cancellationToken).ConfigureAwait(false);

        var response = new CacheStatusResponse
        {
            IsHealthy = isHealthy,
            IsUsingFallback = cacheHealthChecker.IsUsingFallback,
            Timestamp = DateTimeOffset.UtcNow
        };

        return Results.Json(
            ApiResponse<CacheStatusResponse>.CreateSuccess(response),
            CacheAdminJsonContext.Default.ApiResponseCacheStatusResponse);
    }

    private static async Task<IResult> HandleInvalidateCache(
        [FromBody] CacheInvalidationRequest request,
        [FromServices] OutputCacheInvalidationService invalidationService,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ILogger<CacheAdminEndpointsLog> logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Scope))
        {
            return Results.Json(
                ApiResponse<object>.Failure("Scope is required."),
                CacheAdminJsonContext.Default.ApiResponseObject,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var user = context.User.Identity?.Name ?? "unknown";
        AdminLog.CacheInvalidationRequested(logger, request.Scope, user);

        try
        {
            switch (request.Scope.ToLowerInvariant())
            {
                case "layer":
                    if (string.IsNullOrWhiteSpace(request.ServiceId) || request.LayerId is null)
                    {
                        return Results.Json(
                            ApiResponse<object>.Failure("ServiceId and LayerId are required for layer scope."),
                            CacheAdminJsonContext.Default.ApiResponseObject,
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                    await invalidationService.InvalidateLayerAsync(
                        request.ServiceId, request.LayerId, cancellationToken).ConfigureAwait(false);
                    break;
                case "service":
                    if (string.IsNullOrWhiteSpace(request.ServiceId))
                    {
                        return Results.Json(
                            ApiResponse<object>.Failure("ServiceId is required for service scope."),
                            CacheAdminJsonContext.Default.ApiResponseObject,
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                    var layerIds = await ResolveServiceLayerIdsAsync(graphProvider, request.ServiceId, cancellationToken).ConfigureAwait(false);
                    await invalidationService.InvalidateServiceCatalogAsync(
                        request.ServiceId, layerIds, cancellationToken).ConfigureAwait(false);
                    break;
                case "collection":
                    if (string.IsNullOrWhiteSpace(request.CollectionId))
                    {
                        return Results.Json(
                            ApiResponse<object>.Failure("CollectionId is required for collection scope."),
                            CacheAdminJsonContext.Default.ApiResponseObject,
                            statusCode: StatusCodes.Status400BadRequest);
                    }
                    await invalidationService.InvalidateCollectionAsync(
                        request.CollectionId, cancellationToken).ConfigureAwait(false);
                    break;
                case "ogc-metadata":
                    await invalidationService.InvalidateOgcMetadataAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "all":
                    await Task.WhenAll(
                        invalidationService.InvalidateLayerAsync(null, null, cancellationToken),
                        invalidationService.InvalidateServiceCatalogAsync(null, null, cancellationToken),
                        invalidationService.InvalidateOgcMetadataAsync(cancellationToken)
                    ).ConfigureAwait(false);
                    break;
                default:
                    return Results.Json(
                        ApiResponse<object>.Failure($"Unknown invalidation scope: '{request.Scope}'. Valid scopes: layer, service, collection, ogc-metadata, all."),
                        CacheAdminJsonContext.Default.ApiResponseObject,
                        statusCode: StatusCodes.Status400BadRequest);
            }

            AdminLog.CacheInvalidationCompleted(logger, request.Scope);

            var response = new CacheInvalidationResponse
            {
                Success = true,
                Scope = request.Scope,
                Message = $"Cache invalidation completed for scope '{request.Scope}'.",
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(
                ApiResponse<CacheInvalidationResponse>.CreateSuccess(response),
                CacheAdminJsonContext.Default.ApiResponseCacheInvalidationResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdminLog.CacheInvalidationFailed(logger, ex, request.Scope);

            var errorResponse = new CacheInvalidationResponse
            {
                Success = false,
                Scope = request.Scope,
                Message = CacheInvalidationFailedMessage,
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(
                new ApiResponse<CacheInvalidationResponse> { Success = false, Data = errorResponse },
                CacheAdminJsonContext.Default.ApiResponseCacheInvalidationResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<int[]> ResolveServiceLayerIdsAsync(
        IMetadataV2GraphProvider graphProvider,
        string serviceId,
        CancellationToken cancellationToken)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = snapshot.FindService(serviceId);
        if (service is null)
        {
            return [];
        }

        return snapshot.PublicationsForService(service.Metadata.Id)
            .Where(p => p.LayerIndex.HasValue)
            .Select(p => p.LayerIndex!.Value)
            .ToArray();
    }
}
