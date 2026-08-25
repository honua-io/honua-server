// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Health;

/// <summary>
/// Evaluates whether the server is ready to accept traffic.
/// </summary>
public interface IReadinessCheckService
{
    /// <summary>
    /// Performs all readiness checks and returns the aggregate result.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregate readiness result.</returns>
    Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
}
