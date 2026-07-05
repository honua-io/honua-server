// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

/// <summary>
/// Deterministic ops-findings engine. Evaluates a fixed rule set against live operational signals
/// on demand and, for findings that carry a safe recommended fix, materializes that fix through the
/// approval-gated operation gateway. No model/LLM reasoning happens server-side (ADR-0028): every
/// finding and recommended action is produced by deterministic rules, and every change is
/// approval-gated.
/// </summary>
public interface IOpsFindingsService
{
    /// <summary>
    /// Evaluates all rules against current signals and returns the findings that are active now.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The active findings, ordered by descending severity.</returns>
    Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-evaluates findings, locates the one with <paramref name="findingId"/>, and routes its
    /// recommended action through the operation gateway (idempotency-keyed on the finding id).
    /// </summary>
    /// <param name="findingId">The deterministic finding identifier to propose.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The proposal outcome, including the created proposal id when the gateway required approval,
    /// or a not-found/no-action status when the finding vanished or has no recommended action.
    /// </returns>
    Task<OpsFindingProposalResult> ProposeAsync(string findingId, CancellationToken cancellationToken = default);
}
