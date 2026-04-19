// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Transport-neutral input to <see cref="Abstractions.IExecutionAdmissionEvaluator"/>.
/// Carries scoping identifiers for rate, concurrency, cost, and backpressure gates.
/// </summary>
public sealed record ExecutionAdmissionRequest
{
    /// <summary>
    /// Workload category being submitted. Scopes concurrency and cost buckets per kind.
    /// </summary>
    public required ExecutionJobKind JobKind { get; init; }

    /// <summary>
    /// Scoping key for concurrency and cost buckets (for example, workspace or tenant id).
    /// Null collapses to a global partition bucket.
    /// </summary>
    public string? PartitionKey { get; init; }

    /// <summary>
    /// Scoping key for per-principal rate limiting. Null disables rate checks for the request.
    /// </summary>
    public string? PrincipalId { get; init; }

    /// <summary>
    /// Estimated cost weight for the request. 1.0 represents a single-step standard job.
    /// Callers may pass <see cref="AnalysisPlan.Steps"/> count or a dry-run estimate.
    /// </summary>
    public double? EstimatedCostWeight { get; init; }

    /// <summary>
    /// Relative operator priority. Propagated to the job record on admission.
    /// </summary>
    public OperationPriority Priority { get; init; } = OperationPriority.Normal;
}
