// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// Deterministic detection of material ambiguity in a grounding pass. ADR-0027
/// requires clarification for "materially different interpretations" but does
/// not define the rule set; this type pins the rules so honua-server-734 eval
/// harnesses and honua-sdk-js-29 UI flows have a single source of truth.
///
/// A finding is additive — a single grounding pass can surface multiple
/// findings (e.g. ambiguous dataset + publish action). The clarification
/// envelope carries every finding so the caller can resolve them in one turn.
/// </summary>
public sealed record MaterialAmbiguityFinding
{
    /// <summary>
    /// Canonical reason code. Values mirror
    /// <see cref="Geoprocessing.Domain.ClarificationReasonCode"/> so the MCP
    /// clarification envelope carries the same vocabulary.
    /// </summary>
    public required ClarificationReasonCode ReasonCode { get; init; }

    /// <summary>
    /// Stable question identifier the caller will echo back in the
    /// clarification response.
    /// </summary>
    public required string QuestionId { get; init; }

    /// <summary>
    /// Interaction kind (single-select, multi-select, free-text, confirmation).
    /// </summary>
    public required ClarificationQuestionKind QuestionKind { get; init; }

    /// <summary>
    /// Human-readable prompt text.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Option list for select-style questions. Null for free-text or
    /// confirmation.
    /// </summary>
    public IReadOnlyList<ClarificationOption>? Options { get; init; }
}
