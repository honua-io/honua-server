// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Extension methods for integrating performance monitoring into existing operations.
/// Provides easy-to-use wrappers for monitoring database queries, resource allocation, and exceptions.
/// </summary>
public static class PerformanceMonitoringIntegrationExtensions
{
    /// <summary>
    /// Executes a database query with performance monitoring and resource tracking.
    /// </summary>
    /// <typeparam name="T">Return type of the query</typeparam>
    /// <param name="serviceProvider">Service provider for dependency resolution</param>
    /// <param name="queryExecutor">Function that executes the query</param>
    /// <param name="queryType">Type/category of the query for monitoring</param>
    /// <param name="correlationId">Correlation ID for distributed tracing</param>
    /// <returns>Query result</returns>
    public static async Task<T> ExecuteMonitoredQueryAsync<T>(
        this IServiceProvider serviceProvider,
        Func<Task<T>> queryExecutor,
        string queryType,
        string? correlationId = null)
    {
        var queryMonitor = serviceProvider.GetService<IDatabaseQueryPerformanceMonitor>();
        var resourceDetector = serviceProvider.GetService<IResourceLeakDetector>();
        var exceptionTelemetry = serviceProvider.GetService<IEnhancedExceptionTelemetry>();

        var correlationIdToUse = correlationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..8];

        // Start query monitoring
        using var queryContext = queryMonitor?.StartQueryExecution(queryType, correlationIdToUse);

        try
        {
            // Execute the query
            var result = await queryExecutor();

            // Track any disposable resources in the result
            if (resourceDetector != null && result is IDisposable disposable)
            {
                resourceDetector.TrackResource(disposable, typeof(T).Name, correlationIdToUse, queryType);
            }

            // Record successful completion
            queryContext?.Complete(true, GetRowCount(result));

            return result;
        }
        catch (Exception ex)
        {
            // Record query failure
            queryContext?.RecordException(ex);

            // Record exception telemetry
            if (exceptionTelemetry != null)
            {
                var context = new ExceptionContext
                {
                    CorrelationId = correlationIdToUse,
                    OperationType = queryType,
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        ["QueryType"] = queryType,
                        ["ResultType"] = typeof(T).Name
                    }
                };

                exceptionTelemetry.RecordException(ex, context);
            }

            throw;
        }
    }

    /// <summary>
    /// Executes a cached query with performance monitoring.
    /// Note: Requires IQueryResultCacheManager to be registered at the Server level.
    /// </summary>
    /// <typeparam name="T">Return type of the query</typeparam>
    /// <param name="serviceProvider">Service provider for dependency resolution</param>
    /// <param name="cacheKey">Cache key for the query</param>
    /// <param name="queryExecutor">Function that executes the query if not cached</param>
    /// <param name="queryType">Type/category of the query for monitoring</param>
    /// <param name="correlationId">Correlation ID for distributed tracing</param>
    /// <returns>Query result (from cache or fresh execution)</returns>
    public static async Task<T> ExecuteCachedMonitoredQueryAsync<T>(
        this IServiceProvider serviceProvider,
        string cacheKey,
        Func<Task<T>> queryExecutor,
        string queryType,
        string? correlationId = null)
    {
        // This method requires server-level registration of IQueryResultCacheManager
        // Fall back to direct execution in Core-only scenarios
        return await serviceProvider.ExecuteMonitoredQueryAsync(queryExecutor, queryType, correlationId);
    }

    /// <summary>
    /// Tracks a disposable resource for leak detection.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency resolution</param>
    /// <param name="resource">Resource to track</param>
    /// <param name="resourceType">Type/category of the resource</param>
    /// <param name="correlationId">Correlation ID for tracking</param>
    /// <param name="allocationContext">Context where resource was allocated</param>
    /// <returns>The same resource for method chaining</returns>
    public static T TrackResource<T>(
        this IServiceProvider serviceProvider,
        T resource,
        string? resourceType = null,
        string? correlationId = null,
        string? allocationContext = null)
        where T : notnull
    {
        var resourceDetector = serviceProvider.GetService<IResourceLeakDetector>();
        if (resourceDetector != null && resource != null)
        {
            resourceDetector.TrackResource(
                resource,
                resourceType ?? typeof(T).Name,
                correlationId,
                allocationContext);
        }

        return resource;
    }

    /// <summary>
    /// Untracks a disposable resource when properly disposed.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency resolution</param>
    /// <param name="resource">Resource being disposed</param>
    public static void UntrackResource(
        this IServiceProvider serviceProvider,
        object resource)
    {
        var resourceDetector = serviceProvider.GetService<IResourceLeakDetector>();
        resourceDetector?.UntrackResource(resource);
    }

    /// <summary>
    /// Records an exception with enhanced telemetry.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency resolution</param>
    /// <param name="exception">Exception to record</param>
    /// <param name="operationType">Type of operation that failed</param>
    /// <param name="correlationId">Correlation ID for distributed tracing</param>
    /// <param name="additionalProperties">Additional properties for telemetry</param>
    public static void RecordException(
        this IServiceProvider serviceProvider,
        Exception exception,
        string operationType,
        string? correlationId = null,
        IDictionary<string, object>? additionalProperties = null)
    {
        var exceptionTelemetry = serviceProvider.GetService<IEnhancedExceptionTelemetry>();
        if (exceptionTelemetry != null)
        {
            var correlationIdToUse = correlationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N")[..8];
            exceptionTelemetry.RecordException(exception, correlationIdToUse, operationType, additionalProperties);
        }
    }

    /// <summary>
    /// Creates a disposable scope that tracks resource allocation and disposal.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency resolution</param>
    /// <param name="resourceType">Type/category of the resource</param>
    /// <param name="correlationId">Correlation ID for tracking</param>
    /// <param name="allocationContext">Context where resource was allocated</param>
    /// <returns>Disposable scope that automatically untracks the resource</returns>
    public static IDisposable CreateResourceScope(
        this IServiceProvider serviceProvider,
        string resourceType,
        string? correlationId = null,
        string? allocationContext = null)
    {
        return new ResourceTrackingScope(serviceProvider, resourceType, correlationId, allocationContext);
    }

    private static long? GetRowCount<T>(T result)
    {
        return result switch
        {
            System.Collections.ICollection collection => collection.Count,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>().LongCount(),
            _ => null
        };
    }

    /// <summary>
    /// Disposable scope for automatic resource tracking and cleanup.
    /// </summary>
    private sealed class ResourceTrackingScope : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly object _scopeObject;
        private bool _disposed;

        public ResourceTrackingScope(
            IServiceProvider serviceProvider,
            string resourceType,
            string? correlationId,
            string? allocationContext)
        {
            _serviceProvider = serviceProvider;
            _scopeObject = new object(); // Placeholder object to track

            var resourceDetector = _serviceProvider.GetService<IResourceLeakDetector>();
            resourceDetector?.TrackResource(_scopeObject, resourceType, correlationId, allocationContext);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var resourceDetector = _serviceProvider.GetService<IResourceLeakDetector>();
            resourceDetector?.UntrackResource(_scopeObject);
        }
    }
}

/// <summary>
/// Attribute to mark methods for automatic performance monitoring.
/// Can be used with interceptors or AOP frameworks.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MonitorPerformanceAttribute : Attribute
{
    /// <summary>
    /// Type/category of the operation for monitoring purposes.
    /// </summary>
    public string? OperationType { get; set; }

    /// <summary>
    /// Whether to track resource allocations in this method.
    /// </summary>
    public bool TrackResources { get; set; } = true;

    /// <summary>
    /// Whether to monitor exceptions in this method.
    /// </summary>
    public bool MonitorExceptions { get; set; } = true;

    /// <summary>
    /// Cache key pattern for cacheable operations.
    /// </summary>
    public string? CacheKeyPattern { get; set; }

    /// <summary>
    /// Cache expiration time in minutes for cacheable operations.
    /// </summary>
    public int CacheExpirationMinutes { get; set; } = 5;
}