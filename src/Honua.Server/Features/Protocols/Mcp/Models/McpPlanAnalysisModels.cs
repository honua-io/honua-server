// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Mcp.Models;

/// <summary>
/// Arguments for the <c>honua_plan_analysis</c> tool. Accepts a natural-language
/// intent and an optional caller-supplied context blob; the planner echoes the
/// context back unchanged so traceability/correlation hints can flow through
/// fixture replay without the server having to interpret them.
/// </summary>
internal sealed class McpPlanAnalysisArgument
{
    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

    [JsonPropertyName("context")]
    public JsonElement? Context { get; set; }
}

/// <summary>
/// Output for the <c>honua_plan_analysis</c> tool. At most one of
/// <see cref="Plan"/> and <see cref="SpecDraft"/> is populated per call — the
/// planner picks whichever output is appropriate for the matched workflow
/// family, and surfaces non-success states via <see cref="Status"/>.
/// </summary>
internal sealed class McpPlanAnalysisOutput
{
    /// <summary>
    /// One of <c>planned</c>, <c>rejected</c>, <c>clarification_required</c>,
    /// <c>unsupported</c>. Mirrors the fixture envelope shape so the SDK can
    /// branch on a single discriminator instead of structural sniffing.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("plan")]
    public McpAnalysisPlanOutput? Plan { get; set; }

    [JsonPropertyName("specDraft")]
    public McpSpecDraftOutput? SpecDraft { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<McpPlanAnalysisWarning> Warnings { get; set; } = [];

    [JsonPropertyName("cache")]
    public McpPlanAnalysisCache? Cache { get; set; }

    [JsonPropertyName("capabilityState")]
    public McpPlanAnalysisCapabilityState? CapabilityState { get; set; }

    [JsonPropertyName("clarification")]
    public McpPlanAnalysisClarification? Clarification { get; set; }

    /// <summary>
    /// Estimate envelope present when the planner rejected the prompt because
    /// the predicted feature count exceeds the apply limit.
    /// </summary>
    [JsonPropertyName("estimate")]
    public McpPlanAnalysisEstimate? Estimate { get; set; }

    /// <summary>
    /// Fixture case name (<c>success</c>, <c>ambiguity</c>, …) that produced
    /// this response. Empty when the planner ran a non-fixture path.
    /// </summary>
    [JsonPropertyName("fixtureCase")]
    public string? FixtureCase { get; set; }

    /// <summary>
    /// Contract version of the matched fixture (e.g.
    /// <c>honua.ai_builder.spatial_query.v1</c>) for traceability.
    /// </summary>
    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; set; }

    /// <summary>
    /// Human-readable reason populated for non-<c>planned</c> statuses.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Pass-through of the caller's <c>context</c> field, when supplied.
    /// </summary>
    [JsonPropertyName("context")]
    public JsonElement? Context { get; set; }
}

/// <summary>
/// Wire shape for an <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisPlan"/>.
/// Mirrors <see cref="McpPlanInput"/> intentionally so validate/dry-run/execute
/// can consume the planner's output without re-shaping.
/// </summary>
internal sealed class McpAnalysisPlanOutput
{
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public IReadOnlyList<McpAnalysisPlanStepOutput> Steps { get; set; } = [];

    [JsonPropertyName("outputs")]
    public IReadOnlyList<string> Outputs { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

internal sealed class McpAnalysisPlanStepOutput
{
    [JsonPropertyName("stepId")]
    public string StepId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("processId")]
    public string? ProcessId { get; set; }

    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string> DependsOn { get; set; } = [];

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string> Inputs { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Wire shape for a <see cref="Honua.Core.Features.Spec.Domain.CanonicalSpecDocument"/>.
/// Used by the operations-dashboard app-builder family where the planner emits
/// a canonical spec rather than (or alongside) an <see cref="McpAnalysisPlanOutput"/>.
/// </summary>
internal sealed class McpSpecDraftOutput
{
    [JsonPropertyName("reviewStatus")]
    public string ReviewStatus { get; set; } = "reviewable";

    [JsonPropertyName("specKind")]
    public string SpecKind { get; set; } = "CanonicalSpecDocument";

    [JsonPropertyName("grammarVersion")]
    public string GrammarVersion { get; set; } = string.Empty;

    [JsonPropertyName("processFamilyVersion")]
    public string ProcessFamilyVersion { get; set; } = string.Empty;

    [JsonPropertyName("specId")]
    public string? SpecId { get; set; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<McpSpecDraftNodeOutput> Nodes { get; set; } = [];
}

internal sealed class McpSpecDraftNodeOutput
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("op")]
    public string? Op { get; set; }

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string> Inputs { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string> Parameters { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed class McpPlanAnalysisWarning
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class McpPlanAnalysisCache
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("hit")]
    public bool Hit { get; set; }

    [JsonPropertyName("reproducibility")]
    public string? Reproducibility { get; set; }
}

internal sealed class McpPlanAnalysisCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal sealed class McpPlanAnalysisClarification
{
    [JsonPropertyName("candidates")]
    public IReadOnlyList<McpPlanAnalysisClarificationCandidate> Candidates { get; set; } = [];
}

internal sealed class McpPlanAnalysisClarificationCandidate
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("options")]
    public IReadOnlyList<string> Options { get; set; } = [];
}

internal sealed class McpPlanAnalysisEstimate
{
    [JsonPropertyName("featureCount")]
    public long FeatureCount { get; set; }

    [JsonPropertyName("limit")]
    public long Limit { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}
