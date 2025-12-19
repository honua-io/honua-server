// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Service for orchestrating readiness health checks
/// </summary>
public interface IReadinessCheckService
{
    /// <summary>
    /// Performs all readiness checks and returns the overall result
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Readiness check result</returns>
    Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
}
