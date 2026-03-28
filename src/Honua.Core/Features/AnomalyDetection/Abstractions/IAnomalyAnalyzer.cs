// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnomalyDetection.Domain;

namespace Honua.Core.Features.AnomalyDetection.Abstractions;

/// <summary>
/// Analyzes a layer's data for geometry and attribute anomalies.
/// Implementations are expected to be deterministic and database-backed.
/// </summary>
public interface IAnomalyAnalyzer
{
    /// <summary>
    /// Performs anomaly analysis on the specified layer, detecting both
    /// geometry issues and attribute outliers.
    /// </summary>
    /// <param name="request">The analysis request with table and field metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A complete anomaly report.</returns>
    Task<AnomalyReport> AnalyzeAsync(
        AnomalyAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
