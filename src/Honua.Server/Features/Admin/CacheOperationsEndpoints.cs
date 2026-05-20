// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for cache health monitoring and invalidation operations.
/// </summary>
internal static class CacheOperationsEndpoints
{
    private const string CacheHealthCheckFailedMessage = "Cache health check failed.";
    private const string CacheInvalidationFailedMessage = "Cache invalidation failed.";
    public static void MapCacheOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/operations/cache")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Operations")
            .RequireAdminAuthorization();

        group.MapGet("/health", HandleGetCacheHealth)
            .WithDisplayName("Get Cache Health")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<CacheHealthResponse>>();

        group.MapGet("/statistics", HandleGetCacheStatistics)
            .WithDisplayName("Get Cache Statistics")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<CacheStatisticsResponse>>();

        group.MapGet("/redis", HandleGetRedisMetrics)
            .WithDisplayName("Get Redis Cache Metrics")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }))
            .Produces<ApiResponse<RedisConnectionMetricsResponse>>();

        group.MapPost("/invalidate", HandleInvalidateCache)
            .WithDisplayName("Invalidate Cache")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .Produces<ApiResponse<CacheOperationsInvalidationResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandleGetCacheHealth(
        [FromServices] ICacheHealthChecker healthChecker,
        [FromServices] IOptions<CacheOptions> cacheOptions,
        HttpContext context,
        ILogger<CacheOperationsEndpointsLog> logger)
    {
        try
        {
            var options = cacheOptions.Value;
            var isHealthy = await healthChecker.IsCacheHealthyAsync(context.RequestAborted).ConfigureAwait(false);

            RedisServerInfoResponse? redisInfo = null;
            var redis = context.RequestServices.GetService<IConnectionMultiplexer>();
            if (redis is not null)
            {
                redisInfo = await BuildRedisInfoAsync(redis, logger).ConfigureAwait(false);
            }

            AdminLog.CacheHealthCheckCompleted(logger, isHealthy, healthChecker.IsUsingFallback);

            var response = new CacheHealthResponse
            {
                IsHealthy = isHealthy,
                IsUsingFallback = healthChecker.IsUsingFallback,
                CacheEnabled = options.Enabled,
                FallbackEnabled = options.EnableFallback,
                KeyPrefix = options.KeyPrefix,
                DefaultTtlSeconds = options.DefaultTtlSeconds,
                RetryIntervalSeconds = options.RetryIntervalSeconds,
                RedisServerInfo = redisInfo,
                MetricsEndpoint = "/api/v1/admin/operations/cache/statistics",
                GeneratedAt = DateTimeOffset.UtcNow
            };

            return Results.Json(
                ApiResponse<CacheHealthResponse>.CreateSuccess(response),
                CacheOperationsJsonContext.Default.ApiResponseCacheHealthResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdminLog.CacheHealthCheckFailed(logger, ex);

            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                CacheHealthCheckFailedMessage);
        }
    }

    private static async Task<IResult> HandleGetCacheStatistics(
        [FromServices] ICacheMetricsSnapshotProvider cacheMetricsSnapshotProvider,
        [FromServices] ICacheStorageMetricsProvider storageMetricsProvider,
        HttpContext context,
        ILogger<CacheOperationsEndpointsLog> logger)
    {
        try
        {
            var snapshot = cacheMetricsSnapshotProvider.GetCacheMetricsSnapshot();
            var storage = await storageMetricsProvider.GetStorageMetricsAsync(context.RequestAborted).ConfigureAwait(false);
            var totalLookups = snapshot.TotalHits + snapshot.TotalMisses;

            var response = new CacheStatisticsResponse
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                Hits = snapshot.TotalHits,
                Misses = snapshot.TotalMisses,
                Evictions = snapshot.TotalEvictions,
                HitRatio = Ratio(snapshot.TotalHits, totalLookups),
                MissRatio = Ratio(snapshot.TotalMisses, totalLookups),
                Backend = storage.Backend,
                IsUsingFallback = storage.IsUsingFallback,
                LocalKeyCount = storage.LocalKeyCount,
                DistributedKeyCount = storage.DistributedKeyCount,
                RedisDatabaseSize = storage.RedisDatabaseSize,
                RedisUsedMemoryBytes = storage.RedisUsedMemoryBytes,
                RedisUsedMemoryHuman = storage.RedisUsedMemoryHuman,
                Types = snapshot.Types.ToDictionary(
                    static kvp => kvp.Key,
                    static kvp =>
                    {
                        var lookups = kvp.Value.Hits + kvp.Value.Misses;
                        return new CacheTypeStatisticsResponse
                        {
                            Hits = kvp.Value.Hits,
                            Misses = kvp.Value.Misses,
                            Evictions = kvp.Value.Evictions,
                            HitRatio = Ratio(kvp.Value.Hits, lookups),
                            MissRatio = Ratio(kvp.Value.Misses, lookups)
                        };
                    },
                    StringComparer.OrdinalIgnoreCase)
            };

            return Results.Json(
                ApiResponse<CacheStatisticsResponse>.CreateSuccess(response),
                CacheOperationsJsonContext.Default.ApiResponseCacheStatisticsResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AdminLog.CacheHealthCheckFailed(logger, ex);

            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "Cache statistics retrieval failed.");
        }
    }

    private static async Task<IResult> HandleGetRedisMetrics(
        HttpContext context,
        ILogger<CacheOperationsEndpointsLog> logger)
    {
        var redis = context.RequestServices.GetService<IConnectionMultiplexer>();
        if (redis is null)
        {
            var unconfigured = new RedisConnectionMetricsResponse
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                IsConfigured = false,
                IsConnected = false
            };

            return Results.Json(
                ApiResponse<RedisConnectionMetricsResponse>.CreateSuccess(unconfigured),
                CacheOperationsJsonContext.Default.ApiResponseRedisConnectionMetricsResponse);
        }

        var counters = redis.GetCounters();
        var response = new RedisConnectionMetricsResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            IsConfigured = true,
            IsConnected = redis.IsConnected,
            ClientName = redis.ClientName,
            TotalOutstanding = counters.TotalOutstanding,
            EndpointCount = redis.GetEndPoints().Length,
            ServerInfo = await BuildRedisInfoAsync(redis, logger).ConfigureAwait(false)
        };

        return Results.Json(
            ApiResponse<RedisConnectionMetricsResponse>.CreateSuccess(response),
            CacheOperationsJsonContext.Default.ApiResponseRedisConnectionMetricsResponse);
    }

    private static async Task<IResult> HandleInvalidateCache(
        [FromBody] CacheOperationsInvalidationRequest request,
        [FromServices] OutputCacheInvalidationService invalidationService,
        [FromServices] IMetadataV2GraphProvider graphProvider,
        [FromServices] ICacheService cacheService,
        HttpContext context,
        ILogger<CacheOperationsEndpointsLog> logger)
    {
        var hasKeyPattern = !string.IsNullOrWhiteSpace(request.KeyPattern);
        var hasServiceId = !string.IsNullOrWhiteSpace(request.ServiceId);
        var hasScope = !string.IsNullOrWhiteSpace(request.Scope);

        if (!hasKeyPattern && !hasServiceId && !hasScope)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                "At least one of keyPattern, serviceId, or scope must be provided.");
        }

        try
        {
            string scope;
            string? detail;

            if (hasKeyPattern)
            {
                scope = "pattern";
                detail = string.Concat("Pattern: ", request.KeyPattern);
                var scopeLabel = string.Concat("pattern:", request.KeyPattern);
                AdminLog.CacheInvalidationRequested(logger, scopeLabel);
                await cacheService.RemoveByPatternAsync(request.KeyPattern!, context.RequestAborted).ConfigureAwait(false);
            }
            else if (hasScope && string.Equals(request.Scope, "ogc-metadata", StringComparison.OrdinalIgnoreCase))
            {
                scope = "ogc-metadata";
                detail = "OGC metadata cache invalidated";
                AdminLog.CacheInvalidationRequested(logger, scope);
                await invalidationService.InvalidateOgcMetadataAsync(context.RequestAborted).ConfigureAwait(false);
            }
            else if (hasScope && string.Equals(request.Scope, "all", StringComparison.OrdinalIgnoreCase))
            {
                scope = "all";
                detail = "All response and metadata caches invalidated";
                AdminLog.CacheInvalidationRequested(logger, scope);
                await Task.WhenAll(
                    invalidationService.InvalidateLayerAsync(null, null, context.RequestAborted),
                    invalidationService.InvalidateServiceCatalogAsync(null, null, context.RequestAborted),
                    invalidationService.InvalidateOgcMetadataAsync(context.RequestAborted)).ConfigureAwait(false);
            }
            else if (hasServiceId && request.LayerId.HasValue)
            {
                scope = "layer";
                detail = string.Concat("Service: ", request.ServiceId, ", Layer: ", request.LayerId);
                var scopeLabel = string.Concat("layer:", request.ServiceId, "/", request.LayerId);
                AdminLog.CacheInvalidationRequested(logger, scopeLabel);
                await invalidationService.InvalidateLayerAsync(request.ServiceId, request.LayerId, context.RequestAborted).ConfigureAwait(false);
            }
            else if (hasServiceId)
            {
                scope = "service";
                detail = string.Concat("Service: ", request.ServiceId);
                var scopeLabel = string.Concat("service:", request.ServiceId);
                AdminLog.CacheInvalidationRequested(logger, scopeLabel);
                var layerIds = await ResolveServiceLayerIdsAsync(graphProvider, request.ServiceId!, context.RequestAborted).ConfigureAwait(false);
                await invalidationService.InvalidateServiceCatalogAsync(request.ServiceId, layerIds, context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid invalidation request. Provide keyPattern, serviceId, or scope.");
            }

            var response = new CacheOperationsInvalidationResponse
            {
                Invalidated = true,
                Scope = scope,
                Detail = detail
            };

            return Results.Json(
                ApiResponse<CacheOperationsInvalidationResponse>.CreateSuccess(response),
                CacheOperationsJsonContext.Default.ApiResponseCacheOperationsInvalidationResponse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorScope = hasKeyPattern ? string.Concat("pattern:", request.KeyPattern)
                : hasScope ? request.Scope!
                : hasServiceId ? string.Concat("service:", request.ServiceId)
                : "unknown";

            AdminLog.CacheOperationInvalidationFailed(logger, ex, errorScope);

            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                CacheInvalidationFailedMessage);
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

    private static async Task<RedisServerInfoResponse> BuildRedisInfoAsync(IConnectionMultiplexer redis, ILogger logger)
    {
        if (!redis.IsConnected)
        {
            return new RedisServerInfoResponse { IsConnected = false };
        }

        try
        {
            var endpoints = redis.GetEndPoints();
            if (endpoints.Length == 0)
            {
                return new RedisServerInfoResponse { IsConnected = true };
            }

            var server = redis.GetServer(endpoints[0]);
            var info = await server.InfoAsync().ConfigureAwait(false);
            var db = redis.GetDatabase();
            var dbSize = await db.ExecuteAsync("DBSIZE").ConfigureAwait(false);

            string? version = null;
            string? usedMemory = null;
            int? connectedClients = null;
            long? uptimeSeconds = null;

            foreach (var section in info)
            {
                foreach (var kvp in section)
                {
                    switch (kvp.Key)
                    {
                        case "redis_version":
                            version = kvp.Value;
                            break;
                        case "used_memory_human":
                            usedMemory = kvp.Value;
                            break;
                        case "connected_clients":
                            if (int.TryParse(kvp.Value, out var clients))
                                connectedClients = clients;
                            break;
                        case "uptime_in_seconds":
                            if (long.TryParse(kvp.Value, out var uptime))
                                uptimeSeconds = uptime;
                            break;
                    }
                }
            }

            return new RedisServerInfoResponse
            {
                IsConnected = true,
                RedisVersion = version,
                UsedMemory = usedMemory,
                ConnectedClients = connectedClients,
                DatabaseSize = (long?)dbSize,
                UptimeSeconds = uptimeSeconds
            };
        }
        catch (Exception ex)
        {
            AdminLog.RedisInfoRetrievalFailed(logger, ex);
            return new RedisServerInfoResponse { IsConnected = redis.IsConnected };
        }
    }

    private static double Ratio(long numerator, long denominator)
        => denominator > 0 ? (double)numerator / denominator : 0.0;
}

/// <summary>
/// Log category for cache operations endpoints.
/// </summary>
internal sealed class CacheOperationsEndpointsLog;
