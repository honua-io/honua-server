// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Interface for monitoring Redis health and providing circuit breaker functionality.
/// </summary>
public interface IRedisHealthMonitor
{
    /// <summary>
    /// Gets a value indicating whether Redis is currently available.
    /// </summary>
    bool IsRedisAvailable { get; }

    /// <summary>
    /// Gets a value indicating whether Redis was ever available since startup.
    /// Used to differentiate between "Redis was never configured" and "Redis went down".
    /// </summary>
    bool WasRedisEverAvailable { get; }

    /// <summary>
    /// Gets a value indicating whether Redis should be retried based on circuit breaker logic.
    /// </summary>
    bool ShouldRetryRedis { get; }

    /// <summary>
    /// Tests Redis connectivity asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Redis is accessible, false otherwise</returns>
    Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default);
}