// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.HealthCheck;

/// <summary>
/// Abstraction for checking database connectivity in health checks
/// </summary>
public interface IDatabaseHealthChecker
{
    /// <summary>
    /// Checks if the database is available and responsive
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if database is healthy, false otherwise</returns>
    Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default);
}