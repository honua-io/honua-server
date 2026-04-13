// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Redis;

/// <summary>
/// Interface for services that depend on Redis with fallback capabilities.
/// </summary>
public interface IRedisService
{
    /// <summary>
    /// Gets a value indicating whether Redis is currently being used by this service.
    /// </summary>
    bool IsUsingRedis { get; }

    /// <summary>
    /// Gets a value indicating whether Redis is configured for this service.
    /// </summary>
    bool IsRedisConfigured { get; }

    /// <summary>
    /// Attempts to restore Redis functionality after a failure.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Redis was successfully restored, false otherwise</returns>
    Task<bool> TryRestoreRedisAsync(CancellationToken cancellationToken = default);
}
