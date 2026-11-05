// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;

namespace Honua.Infrastructure.Redis;

internal static class RedisFallbackStrategy
{
    public static IRedisFallbackStrategy FailFast => FailFastStrategy.Instance;

    public static IRedisFallbackStrategy InMemoryFallback => InMemoryFallbackStrategy.Instance;

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
            // Allow fallback only in development and test environments.
            return hostEnvironment.IsDevelopment() ||
                   hostEnvironment.IsEnvironment("Test");
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
