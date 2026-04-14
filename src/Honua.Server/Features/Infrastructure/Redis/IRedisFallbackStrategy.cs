// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Strategy for handling Redis fallback behavior when Redis is unavailable.
/// </summary>
public interface IRedisFallbackStrategy
{
    /// <summary>
    /// Gets the fallback mode for this strategy.
    /// </summary>
    RedisFallbackMode Mode { get; }

    /// <summary>
    /// Determines whether fallback is allowed based on Redis health and environment.
    /// </summary>
    /// <param name="healthMonitor">Redis health monitor</param>
    /// <param name="hostEnvironment">Host environment information</param>
    /// <returns>True if fallback is allowed, false otherwise</returns>
    bool ShouldAllowFallback(IRedisHealthMonitor healthMonitor, IHostEnvironment hostEnvironment);

    /// <summary>
    /// Gets an exception to throw when Redis is unavailable and fallback is not allowed.
    /// </summary>
    /// <param name="operation">The operation that requires Redis</param>
    /// <param name="lastException">The last Redis exception, if any</param>
    /// <returns>Exception to throw</returns>
    Exception GetUnavailableException(string operation, Exception? lastException = null);
}
