// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.HealthCheck.Abstractions;

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
