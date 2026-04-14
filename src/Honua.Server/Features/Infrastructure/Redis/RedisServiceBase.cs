// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Base class for Redis-dependent services with standardized fallback behavior.
/// </summary>
internal abstract class RedisServiceBase : IRedisService
{
    protected readonly IConnectionMultiplexer? Redis;
    protected readonly IDatabase? Database;
    protected readonly IRedisHealthMonitor HealthMonitor;
    protected readonly IRedisFallbackStrategy FallbackStrategy;
    protected readonly IHostEnvironment HostEnvironment;
    protected readonly ILogger Logger;

    private volatile bool _isUsingRedis;

    protected RedisServiceBase(
        IConnectionMultiplexer? redis,
        IRedisHealthMonitor healthMonitor,
        IRedisFallbackStrategy fallbackStrategy,
        IHostEnvironment hostEnvironment,
        ILogger logger)
    {
        Redis = redis;
        Database = redis?.GetDatabase();
        HealthMonitor = healthMonitor;
        FallbackStrategy = fallbackStrategy;
        HostEnvironment = hostEnvironment;
        Logger = logger;

        _isUsingRedis = redis != null && healthMonitor.IsRedisAvailable;

        LogServiceInitialization();
    }

    public bool IsUsingRedis => _isUsingRedis && HealthMonitor.IsRedisAvailable;
    public bool IsRedisConfigured => Redis != null;
    public RedisFallbackMode FallbackMode => FallbackStrategy.Mode;

    public virtual async Task<bool> TryRestoreRedisAsync(CancellationToken cancellationToken = default)
    {
        if (Database == null)
            return false;

        if (IsUsingRedis)
            return true;

        var restored = await HealthMonitor.TestConnectivityAsync(cancellationToken).ConfigureAwait(false);
        if (restored)
        {
            _isUsingRedis = true;
            OnRedisRestored();
        }

        return restored;
    }

    /// <summary>
    /// Executes a Redis operation with standardized error handling and fallback logic.
    /// </summary>
    /// <param name="operation">Description of the operation for logging</param>
    /// <param name="redisOperation">The Redis operation to execute</param>
    /// <param name="fallbackOperation">Optional fallback operation when Redis is unavailable</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected async Task<T> ExecuteWithFallbackAsync<T>(
        string operation,
        Func<IDatabase, CancellationToken, Task<T>> redisOperation,
        Func<CancellationToken, Task<T>>? fallbackOperation = null,
        CancellationToken cancellationToken = default)
    {
        if (!HealthMonitor.IsRedisAvailable)
        {
            _isUsingRedis = false;
        }

        // Try to restore Redis if it's currently unavailable
        if (!IsUsingRedis && Database != null && HealthMonitor.ShouldRetryRedis)
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        // Attempt Redis operation if available
        if (IsUsingRedis && Database != null)
        {
            try
            {
                var result = await redisOperation(Database, cancellationToken).ConfigureAwait(false);
                HealthMonitor.RecordSuccess();
                return result;
            }
            catch (Exception ex) when (IsRedisException(ex))
            {
                HandleRedisFailure(operation, ex);
            }
        }

        // Handle fallback
        if (FallbackStrategy.ShouldAllowFallback(HealthMonitor, HostEnvironment))
        {
            if (fallbackOperation != null)
            {
                LogFallbackOperation(operation);
                return await fallbackOperation(cancellationToken).ConfigureAwait(false);
            }

            throw new NotSupportedException($"Operation '{operation}' has no fallback implementation");
        }

        // Fallback not allowed - throw appropriate exception
        throw FallbackStrategy.GetUnavailableException(operation);
    }

    /// <summary>
    /// Executes a Redis operation with standardized error handling and fallback logic (synchronous fallback).
    /// </summary>
    protected async Task<T> ExecuteWithFallbackAsync<T>(
        string operation,
        Func<IDatabase, CancellationToken, Task<T>> redisOperation,
        Func<T> fallbackOperation,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithFallbackAsync(
            operation,
            redisOperation,
            _ => Task.FromResult(fallbackOperation()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a Redis operation without fallback (fail-fast).
    /// </summary>
    protected async Task<T> ExecuteRedisOnlyAsync<T>(
        string operation,
        Func<IDatabase, CancellationToken, Task<T>> redisOperation,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithFallbackAsync(
            operation,
            redisOperation,
            fallbackOperation: (Func<CancellationToken, Task<T>>?)null,
            cancellationToken).ConfigureAwait(false);
    }

    private void HandleRedisFailure(string operation, Exception ex)
    {
        HealthMonitor.RecordFailure(ex);
        _isUsingRedis = false;
        LogRedisOperationFailed(operation, ex);
        OnRedisFailure(operation, ex);
    }

    private static bool IsRedisException(Exception ex)
    {
        return ex is RedisException ||
               ex is RedisConnectionException ||
               ex is RedisTimeoutException ||
               ex is RedisServerException;
    }

    /// <summary>
    /// Called when Redis connectivity is restored after a failure.
    /// Override to implement service-specific restoration logic.
    /// </summary>
    protected virtual void OnRedisRestored() { }

    /// <summary>
    /// Called when a Redis operation fails.
    /// Override to implement service-specific failure handling.
    /// </summary>
    protected virtual void OnRedisFailure(string operation, Exception exception) { }

    private void LogServiceInitialization()
    {
        if (Redis != null)
        {
            Logger.LogInformation(
                "Service {ServiceType} initialized with Redis support (strategy: {Strategy})",
                GetType().Name,
                FallbackStrategy.Mode);
        }
        else
        {
            Logger.LogInformation(
                "Service {ServiceType} initialized without Redis (strategy: {Strategy})",
                GetType().Name,
                FallbackStrategy.Mode);
        }
    }

    private void LogFallbackOperation(string operation)
    {
        Logger.LogWarning(
            "Redis unavailable for {Operation} - using fallback (service: {ServiceType})",
            operation,
            GetType().Name);
    }

    private void LogRedisOperationFailed(string operation, Exception ex)
    {
        Logger.LogWarning(ex,
            "Redis operation failed for {Operation} (service: {ServiceType})",
            operation,
            GetType().Name);
    }
}
