// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Coordinates durable alert evaluation and state persistence.
/// </summary>
public interface IAlertPipeline
{
    /// <summary>
    /// Processes a batch of durable feature changes beginning after the provided generation.
    /// </summary>
    /// <param name="lastProcessedGeneration">Last processed generation (exclusive)</param>
    /// <param name="batchSize">Maximum number of changes to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The most recent generation processed, or the input generation if no work was found</returns>
    Task<long> ProcessChangesAsync(
        long lastProcessedGeneration,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a dwell-trigger sweep over state rows that are due for evaluation.
    /// </summary>
    /// <param name="evaluatedAt">Current evaluation timestamp</param>
    /// <param name="batchSize">Maximum number of dwell state rows to evaluate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of dwell candidates evaluated</returns>
    Task<int> SweepDwellAsync(
        DateTimeOffset evaluatedAt,
        int batchSize,
        CancellationToken cancellationToken = default);
}
