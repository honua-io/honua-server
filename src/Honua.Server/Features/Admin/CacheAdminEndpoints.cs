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
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Cache")
            .RequireAdminAuthorization()
            .RequirePlatformAdminAuthority();

        var status = AdminHttpOperationRouteBinding.ForOperate("admin.cache.status", "/cache");
        group.MapMethods(status.RoutePattern, status.Methods, HandleGetCacheStatus)
            .WithAdminOperation(status.Definition)
            .WithDisplayName("Get Cache Status")
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
        var validation = ValidateRequest(request);
        if (validation.Error is not null)
        {
            return CreateBadRequest(validation.Error);
        }

        var normalizedScope = validation.NormalizedScope!;
        var user = context.User.Identity?.Name ?? "unknown";
        AdminLog.CacheInvalidationRequested(logger, normalizedScope, user);

        try
        {
            var invalidationContext = new InvalidationContext(invalidationService, graphProvider, cancellationToken);
            await ExecuteInvalidationAsync(normalizedScope, request, invalidationContext).ConfigureAwait(false);

            AdminLog.CacheInvalidationCompleted(logger, normalizedScope);

            var response = new CacheInvalidationResponse
            {
                Success = true,
                Scope = normalizedScope,
                Message = $"Cache invalidation completed for scope '{normalizedScope}'.",
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(
                ApiResponse<CacheInvalidationResponse>.CreateSuccess(response),
                CacheAdminJsonContext.Default.ApiResponseCacheInvalidationResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdminLog.CacheInvalidationFailed(logger, ex, normalizedScope);

            var errorResponse = new CacheInvalidationResponse
            {
                Success = false,
                Scope = normalizedScope,
                Message = CacheInvalidationFailedMessage,
                Timestamp = DateTimeOffset.UtcNow
            };

            return Results.Json(
                new ApiResponse<CacheInvalidationResponse> { Success = false, Data = errorResponse },
                CacheAdminJsonContext.Default.ApiResponseCacheInvalidationResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static (string? NormalizedScope, string? Error) ValidateRequest(CacheInvalidationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Scope))
        {
            return (null, "Scope is required.");
        }

        var fieldError = CacheInvalidationValidation.ValidateOptionalField(request.Scope, "Scope")
            ?? CacheInvalidationValidation.ValidateOptionalField(request.ServiceId, "ServiceId")
            ?? CacheInvalidationValidation.ValidateOptionalField(request.CollectionId, "CollectionId");
        if (fieldError is not null)
        {
            return (null, fieldError);
        }

        var candidate = request.Scope.Trim().ToLowerInvariant();
        var scopeError = ValidateScopeRequirements(candidate, request);
        return scopeError is not null ? (null, scopeError) : (candidate, null);
    }

    private static string? ValidateScopeRequirements(string normalizedScope, CacheInvalidationRequest request)
        => normalizedScope switch
        {
            "layer" when string.IsNullOrWhiteSpace(request.ServiceId) || request.LayerId is null
                => "ServiceId and LayerId are required for layer scope.",
            "service" when string.IsNullOrWhiteSpace(request.ServiceId)
                => "ServiceId is required for service scope.",
            "collection" when string.IsNullOrWhiteSpace(request.CollectionId)
                => "CollectionId is required for collection scope.",
            "layer" or "service" or "collection" or "ogc-metadata" or "all" => null,
            _ => "Unknown invalidation scope. Valid scopes: layer, service, collection, ogc-metadata, all.",
        };

    private static Task ExecuteInvalidationAsync(
        string normalizedScope,
        CacheInvalidationRequest request,
        InvalidationContext invalidation)
        => normalizedScope switch
        {
            "layer" => invalidation.Service.InvalidateLayerAsync(request.ServiceId!, request.LayerId, invalidation.CancellationToken),
            "service" => InvalidateServiceAsync(request.ServiceId!, invalidation),
            "collection" => invalidation.Service.InvalidateCollectionAsync(request.CollectionId!, invalidation.CancellationToken),
            "ogc-metadata" => invalidation.Service.InvalidateOgcMetadataAsync(invalidation.CancellationToken),
            _ => invalidation.Service.InvalidateEverythingAsync(invalidation.CancellationToken),
        };

    private static async Task InvalidateServiceAsync(string serviceId, InvalidationContext invalidation)
    {
        var layerIds = await ResolveServiceLayerIdsAsync(invalidation.GraphProvider, serviceId, invalidation.CancellationToken).ConfigureAwait(false);
        await invalidation.Service.InvalidateServiceCatalogAsync(serviceId, layerIds, invalidation.CancellationToken).ConfigureAwait(false);
    }

    private static IResult CreateBadRequest(string message)
        => Results.Json(
            ApiResponse<object>.Failure(message),
            CacheAdminJsonContext.Default.ApiResponseObject,
            statusCode: StatusCodes.Status400BadRequest);

    private readonly record struct InvalidationContext(
        OutputCacheInvalidationService Service,
        IMetadataV2GraphProvider GraphProvider,
        CancellationToken CancellationToken);

    internal static async Task<int[]> ResolveServiceLayerIdsAsync(
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
