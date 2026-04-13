// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Standard implementations of Redis fallback strategies.
/// </summary>
internal static class RedisFallbackStrategy
{
    /// <summary>
    /// Fail-fast strategy for critical distributed coordination.
    /// </summary>
    public static IRedisFallbackStrategy FailFast => FailFastStrategy.Instance;

    /// <summary>
    /// In-memory fallback strategy for caching and non-critical operations.
    /// </summary>
    public static IRedisFallbackStrategy InMemoryFallback => InMemoryFallbackStrategy.Instance;

    /// <summary>
    /// Allow local operations in development/test, fail in production.
    /// </summary>
    public static IRedisFallbackStrategy AllowLocalInDev => AllowLocalInDevStrategy.Instance;

    private sealed class FailFastStrategy : IRedisFallbackStrategy
    {
        public static readonly FailFastStrategy Instance = new();
        private FailFastStrategy() { }

        public RedisFallbackMode Mode => RedisFallbackMode.FailFast;

        public bool ShouldAllowFallback(IRedisHealthMonitor healthMonitor, IHostEnvironment hostEnvironment)
            => false;

        public Exception GetUnavailableException(string operation, Exception? lastException = null)
        {
            var message = $"Redis is required for {operation} but is currently unavailable. " +
                         "This operation requires distributed coordination and cannot proceed without Redis.";
            return lastException != null
                ? new InvalidOperationException(message, lastException)
                : new InvalidOperationException(message);
        }
    }

    private sealed class InMemoryFallbackStrategy : IRedisFallbackStrategy
    {
        public static readonly InMemoryFallbackStrategy Instance = new();
        private InMemoryFallbackStrategy() { }

        public RedisFallbackMode Mode => RedisFallbackMode.InMemoryFallback;

        public bool ShouldAllowFallback(IRedisHealthMonitor healthMonitor, IHostEnvironment hostEnvironment)
            => true;

        public Exception GetUnavailableException(string operation, Exception? lastException = null)
        {
            // This strategy always allows fallback, so this method should not be called
            throw new InvalidOperationException("InMemoryFallback strategy should never reject operations");
        }
    }

    private sealed class AllowLocalInDevStrategy : IRedisFallbackStrategy
    {
        public static readonly AllowLocalInDevStrategy Instance = new();
        private AllowLocalInDevStrategy() { }

        public RedisFallbackMode Mode => RedisFallbackMode.AllowLocalInDev;

        public bool ShouldAllowFallback(IRedisHealthMonitor healthMonitor, IHostEnvironment hostEnvironment)
        {
            // Allow fallback in development, test, or if Redis was never configured
            return hostEnvironment.IsDevelopment() ||
                   hostEnvironment.IsEnvironment("Test") ||
                   !healthMonitor.WasRedisEverAvailable;
        }

        public Exception GetUnavailableException(string operation, Exception? lastException = null)
        {
            var message = $"Redis is required for {operation} in production environments but is currently unavailable. " +
                         "This operation requires distributed coordination in production and cannot proceed without Redis.";
            return lastException != null
                ? new InvalidOperationException(message, lastException)
                : new InvalidOperationException(message);
        }
    }
}