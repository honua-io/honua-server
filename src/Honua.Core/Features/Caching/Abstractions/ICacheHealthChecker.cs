// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

/// <summary>
/// Abstraction for checking cache service health.
/// </summary>
public interface ICacheHealthChecker
{
    /// <summary>
    /// Checks if the cache service is available and responsive.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if cache is healthy, false otherwise</returns>
    Task<bool> IsCacheHealthyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether the cache is currently using the fallback (in-memory) mode.
    /// </summary>
    bool IsUsingFallback { get; }
}
