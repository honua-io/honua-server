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
    /// Gets the last successful Redis contact time, if any.
    /// </summary>
    DateTimeOffset? LastSuccessfulContact { get; }

    /// <summary>
    /// Gets the last Redis failure time, if any.
    /// </summary>
    DateTimeOffset? LastFailure { get; }

    /// <summary>
    /// Gets the current consecutive failure count.
    /// </summary>
    int ConsecutiveFailures { get; }

    /// <summary>
    /// Gets a value indicating whether Redis should be retried based on circuit breaker logic.
    /// </summary>
    bool ShouldRetryRedis { get; }

    /// <summary>
    /// Records a successful Redis interaction.
    /// </summary>
    void RecordSuccess();

    /// <summary>
    /// Records a failed Redis interaction.
    /// </summary>
    /// <param name="exception">The failure exception.</param>
    void RecordFailure(Exception exception);

    /// <summary>
    /// Tests Redis connectivity asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Redis is accessible, false otherwise</returns>
    Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default);
}
