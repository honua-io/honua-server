// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// A deterministic entity→candidate binding the grounding service resolved
/// without a clarification turn. Emitted when exactly one candidate of a kind
/// clears the high-confidence floor and dominates any runner-up by more than
/// the configured material spread, so a cold LLM caller can compose a plan
/// without round-tripping an obvious single-candidate choice (honua-server#1949).
///
/// The binding is auditable on purpose: it carries the chosen candidate id, the
/// match score, and the runner-up margin so the guardrails story can explain
/// <i>why</i> the binding resolved silently. It is recorded into the draft
/// intent's provenance assumptions and logged at information level.
/// </summary>
public sealed record ResolvedBinding
{
    /// <summary>
    /// Question slot this binding fills (e.g. <c>dataset.selection</c>). Mirrors
    /// the <see cref="MaterialAmbiguityFinding.QuestionId"/> that would have been
    /// emitted had the candidates been ambiguous, so consumers can correlate an
    /// auto-resolution with the clarification it replaced.
    /// </summary>
    public required string QuestionId { get; init; }

    /// <summary>
    /// Kind of candidate that was bound.
    /// </summary>
    public required CandidateKind Kind { get; init; }

    /// <summary>
    /// Canonical identifier of the bound candidate.
    /// </summary>
    public required string CandidateId { get; init; }

    /// <summary>
    /// Display name of the bound candidate, for human-readable provenance.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Deterministic match score of the bound candidate in [0,1].
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Margin between the bound candidate's score and the next-best candidate of
    /// the same kind. <c>1.0</c> when there was no runner-up (the candidate was
    /// the only one clearing the floor).
    /// </summary>
    public required double RunnerUpMargin { get; init; }
}
