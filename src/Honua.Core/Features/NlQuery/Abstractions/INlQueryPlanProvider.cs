// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Core.Features.NlQuery.Abstractions;

/// <summary>
/// Generates a constrained filter plan from a natural-language spatial query.
/// Implementations target specific LLM backends (OpenAI-compatible, etc.)
/// but always produce the same structured <see cref="FilterPlan"/> output.
/// </summary>
public interface INlQueryPlanProvider
{
    /// <summary>
    /// Generate a constrained filter plan from a natural-language query.
    /// </summary>
    /// <param name="request">The NL query request containing the utterance and layer schema.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing either a <see cref="FilterPlan"/> or an error message.</returns>
    Task<NlQueryPlanResult> GeneratePlanAsync(
        NlQueryPlanRequest request,
        CancellationToken cancellationToken = default);
}
