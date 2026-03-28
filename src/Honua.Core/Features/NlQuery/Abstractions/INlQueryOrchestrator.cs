// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Core.Features.NlQuery.Abstractions;

/// <summary>
/// Orchestrates the end-to-end NL query flow: plan generation, compilation, and validation.
/// </summary>
public interface INlQueryOrchestrator
{
    /// <summary>
    /// Translates a natural-language query into a compiled filter expression.
    /// Resolves layer schema context, invokes the plan provider, and compiles the result.
    /// </summary>
    /// <param name="request">The NL query request with layer context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The orchestration result containing the compiled filter or error details.</returns>
    Task<NlQueryOrchestrationResult> ExecuteAsync(
        NlQueryPlanRequest request,
        CancellationToken cancellationToken = default);
}
