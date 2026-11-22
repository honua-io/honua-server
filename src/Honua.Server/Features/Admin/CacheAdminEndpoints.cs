// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

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

        var status = AdminHttpOperationRouteBinding.ForOperate("admin.cache.status", "/cache");
        group.MapMethods(status.RoutePattern, status.Methods, HandleGetCacheStatus)
            .WithAdminOperation(status.Definition)
            .WithDisplayName("Get Cache Status")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .Produces<ApiResponse<CacheStatusResponse>>();

        var invalidate = AdminHttpOperationRouteBinding.ForOperate("admin.cache.invalidate", "/cache");
        group.MapMethods(invalidate.RoutePattern, invalidate.Methods, HandleInvalidateCache)
            .WithAdminOperation(invalidate.Definition)
            .WithDisplayName("Invalidate Cache")
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
        if (!TryValidateRequest(request, out var normalizedScope, out var validationError))
        {
            return CreateBadRequest(validationError!);
        }

        var user = context.User.Identity?.Name ?? "unknown";
        AdminLog.CacheInvalidationRequested(logger, normalizedScope!, user);

        try
        {
            await ExecuteInvalidationAsync(
                normalizedScope!,
                request,
                invalidationService,
                graphProvider,
                cancellationToken).ConfigureAwait(false);

            AdminLog.CacheInvalidationCompleted(logger, normalizedScope!);

            var response = new CacheInvalidationResponse
            {
                Success = true,
                Scope = normalizedScope!,
                Message = $"Cache invalidation completed for scope '{normalizedScope}'.",
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(
                ApiResponse<CacheInvalidationResponse>.CreateSuccess(response),
                CacheAdminJsonContext.Default.ApiResponseCacheInvalidationResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdminLog.CacheInvalidationFailed(logger, ex, normalizedScope!);

            var errorResponse = new CacheInvalidationResponse
            {
                Success = false,
                Scope = normalizedScope!,
                Message = CacheInvalidationFailedMessage,
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(
                new ApiResponse<CacheInvalidationResponse> { Success = false, Data = errorResponse },
                CacheAdminJsonContext.Default.ApiResponseCacheInvalidationResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static bool TryValidateRequest(
        CacheInvalidationRequest request,
        out string? normalizedScope,
        out string? error)
    {
        normalizedScope = null;
        if (string.IsNullOrWhiteSpace(request.Scope))
        {
            error = "Scope is required.";
            return false;
        }

        if (CacheInvalidationValidation.ContainsControlOrLineBreak(request.Scope))
        {
            error = "Scope must not contain control characters.";
            return false;
        }

        var candidate = request.Scope.Trim().ToLowerInvariant();
        if (candidate is not ("layer" or "service" or "collection" or "ogc-metadata" or "all"))
        {
            error = "Unknown invalidation scope. Valid scopes: layer, service, collection, ogc-metadata, all.";
            return false;
        }

        if (!CacheInvalidationValidation.TryValidateOptionalField(request.ServiceId, "ServiceId", out error) ||
            !CacheInvalidationValidation.TryValidateOptionalField(request.CollectionId, "CollectionId", out error))
        {
            return false;
        }

        if (candidate == "layer" && (string.IsNullOrWhiteSpace(request.ServiceId) || request.LayerId is null))
        {
            error = "ServiceId and LayerId are required for layer scope.";
            return false;
        }

        if (candidate == "service" && string.IsNullOrWhiteSpace(request.ServiceId))
        {
            error = "ServiceId is required for service scope.";
            return false;
        }

        if (candidate == "collection" && string.IsNullOrWhiteSpace(request.CollectionId))
        {
            error = "CollectionId is required for collection scope.";
            return false;
        }

        normalizedScope = candidate;
        error = null;
        return true;
    }

    private static Task ExecuteInvalidationAsync(
        string normalizedScope,
        CacheInvalidationRequest request,
        OutputCacheInvalidationService invalidationService,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
        => normalizedScope switch
        {
            "layer" => invalidationService.InvalidateLayerAsync(request.ServiceId!, request.LayerId, cancellationToken),
            "service" => InvalidateServiceAsync(request.ServiceId!, invalidationService, graphProvider, cancellationToken),
            "collection" => invalidationService.InvalidateCollectionAsync(request.CollectionId!, cancellationToken),
            "ogc-metadata" => invalidationService.InvalidateOgcMetadataAsync(cancellationToken),
            _ => Task.WhenAll(
                invalidationService.InvalidateLayerAsync(null, null, cancellationToken),
                invalidationService.InvalidateServiceCatalogAsync(null, null, cancellationToken),
                invalidationService.InvalidateOgcMetadataAsync(cancellationToken)),
        };

    private static async Task InvalidateServiceAsync(
        string serviceId,
        OutputCacheInvalidationService invalidationService,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        var layerIds = await ResolveServiceLayerIdsAsync(graphProvider, serviceId, cancellationToken).ConfigureAwait(false);
        await invalidationService.InvalidateServiceCatalogAsync(serviceId, layerIds, cancellationToken).ConfigureAwait(false);
    }

    private static IResult CreateBadRequest(string message)
        => Results.Json(
            ApiResponse<object>.Failure(message),
            CacheAdminJsonContext.Default.ApiResponseObject,
            statusCode: StatusCodes.Status400BadRequest);

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
