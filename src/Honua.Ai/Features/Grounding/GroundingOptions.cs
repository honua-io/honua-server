// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Grounding;

/// <summary>
/// Deterministic thresholds that pin the grounding service's material-ambiguity
/// rule set from ADR-0027. Values are defaulted from the AI Operator technical
/// plan's "material interpretation" discussion and are overridable via
/// configuration so honua-server-734 eval runs can re-tune from data without a
/// contract change.
/// </summary>
internal sealed class GroundingOptions
{
    /// <summary>
    /// Configuration section name: <c>Operator:Grounding</c>.
    /// </summary>
    public const string SectionName = "Operator:Grounding";

    /// <summary>
    /// Minimum workflow-family classification confidence required before the
    /// service emits a draft intent without a <c>LowConfidence</c>
    /// clarification. Default 0.60.
    /// </summary>
    public double WorkflowFamilyFloor { get; set; } = 0.60;

    /// <summary>
    /// Score at or above which a candidate is considered "high confidence".
    /// When two or more candidates of the same kind clear this floor within
    /// <see cref="MaterialSpread"/>, the service raises an ambiguity
    /// clarification. Default 0.70.
    /// </summary>
    public double HighConfidenceFloor { get; set; } = 0.70;

    /// <summary>
    /// Score at or above which a candidate is considered "medium confidence".
    /// Default 0.40.
    /// </summary>
    public double MediumConfidenceFloor { get; set; } = 0.40;

    /// <summary>
    /// Maximum score spread between two or more high-confidence candidates
    /// before the service treats them as materially ambiguous. Default 0.05.
    /// </summary>
    public double MaterialSpread { get; set; } = 0.05;

    /// <summary>
    /// Maximum candidates returned per kind. Default 5. Keeps the tool output
    /// compact for LLM callers without losing the ranked shortlist.
    /// </summary>
    public int MaxCandidatesPerKind { get; set; } = 5;
}
