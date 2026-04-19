// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Transport-neutral evaluator that gates operator-execution submission on
/// rate, concurrency, cost, and backpressure controls. Composes with
/// <c>IOperatorAuthorizationEvaluator</c> and <c>IOperatorApprovalEvaluator</c>
/// as the "may you now?" gate after authorization and approval have passed.
/// </summary>
public interface IExecutionAdmissionEvaluator
{
    /// <summary>
    /// Evaluates whether the request may enter the execution pipeline right now.
    /// Returns a decision whose outcome and dimension drive throttling, denial,
    /// and observability without redefining canonical process semantics.
    /// </summary>
    Task<ExecutionAdmissionDecision> EvaluateAsync(
        ExecutionAdmissionRequest request,
        CancellationToken cancellationToken = default);
}
