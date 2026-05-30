// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Grounding;

// -----------------------------------------------------------------------
// Tool argument shapes (tools/call params.arguments)
// -----------------------------------------------------------------------

/// <summary>
/// Arguments for <c>honua_ground_candidates</c>. Mirrors
/// <see cref="Honua.Core.Features.Grounding.Domain.GroundingRequest"/>
/// with a stringly-typed workflow-family hint for schema friendliness.
/// </summary>
internal sealed class McpGroundCandidatesArgument
{
    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    [JsonPropertyName("workflowFamilyHint")]
    public string? WorkflowFamilyHint { get; set; }

    [JsonPropertyName("constraints")]
    public McpIntentConstraintsInput? Constraints { get; set; }

    [JsonPropertyName("explicitInputs")]
    public IReadOnlyList<string>? ExplicitInputs { get; set; }

    [JsonPropertyName("assumptionPolicy")]
    public string? AssumptionPolicy { get; set; }

    [JsonPropertyName("context")]
    public McpCallerContextInput? Context { get; set; }

    [JsonPropertyName("intentId")]
    public string? IntentId { get; set; }
}

/// <summary>
/// Arguments for <c>honua_clarify_intent</c>. Adds a required
/// <see cref="Response"/> payload; the rest of the fields are optional so the
/// caller can carry forward constraints pinned during earlier turns.
/// </summary>
internal sealed class McpClarifyIntentArgument
{
    [JsonPropertyName("intentId")]
    public string? IntentId { get; set; }

    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    [JsonPropertyName("workflowFamilyHint")]
    public string? WorkflowFamilyHint { get; set; }

    [JsonPropertyName("constraints")]
    public McpIntentConstraintsInput? Constraints { get; set; }

    [JsonPropertyName("explicitInputs")]
    public IReadOnlyList<string>? ExplicitInputs { get; set; }

    [JsonPropertyName("assumptionPolicy")]
    public string? AssumptionPolicy { get; set; }

    [JsonPropertyName("context")]
    public McpCallerContextInput? Context { get; set; }

    [JsonPropertyName("response")]
    public McpClarificationResponseInput? Response { get; set; }
}

/// <summary>
/// Caller-context fields accepted on the MCP wire. Null fields flow through
/// to the canonical <see cref="Honua.Core.Features.Grounding.Domain.CallerContext"/>.
/// </summary>
internal sealed class McpCallerContextInput
{
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("priorIntentId")]
    public string? PriorIntentId { get; set; }

    [JsonPropertyName("promotionScope")]
    public string? PromotionScope { get; set; }
}

/// <summary>
/// Intent-constraints shape accepted on the MCP wire.
/// </summary>
internal sealed class McpIntentConstraintsInput
{
    [JsonPropertyName("areaOfInterest")]
    public string? AreaOfInterest { get; set; }

    [JsonPropertyName("spatialReferenceId")]
    public int? SpatialReferenceId { get; set; }

    [JsonPropertyName("timeWindowStart")]
    public DateTimeOffset? TimeWindowStart { get; set; }

    [JsonPropertyName("timeWindowEnd")]
    public DateTimeOffset? TimeWindowEnd { get; set; }

    [JsonPropertyName("units")]
    public string? Units { get; set; }
}

/// <summary>
/// Clarification-response envelope accepted by <c>honua_clarify_intent</c>.
/// </summary>
internal sealed class McpClarificationResponseInput
{
    [JsonPropertyName("answers")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers { get; set; }
}

// -----------------------------------------------------------------------
// Tool output shapes (serialized into structuredContent of the call result)
// -----------------------------------------------------------------------

/// <summary>
/// Envelope returned by both grounding tools. Carries the workflow-family
/// classification, a canonical draft intent, ranked candidates, an optional
/// clarification envelope, and the engine tag used by the eval harness.
/// </summary>
internal sealed class McpGroundingOutput
{
    [JsonPropertyName("workflowFamily")]
    public McpWorkflowFamilyClassification WorkflowFamily { get; set; } = new();

    [JsonPropertyName("draftIntent")]
    public McpDraftIntent DraftIntent { get; set; } = new();

    [JsonPropertyName("candidates")]
    public McpCandidateRanking Candidates { get; set; } = new();

    [JsonPropertyName("clarification")]
    public McpClarificationEnvelope? Clarification { get; set; }

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;
}

internal sealed class McpWorkflowFamilyClassification
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; set; } = [];
}

internal sealed class McpDraftIntent
{
    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = string.Empty;

    [JsonPropertyName("workflowFamily")]
    public string WorkflowFamily { get; set; } = string.Empty;

    [JsonPropertyName("requestedOutputs")]
    public IReadOnlyList<string> RequestedOutputs { get; set; } = [];

    [JsonPropertyName("assumptionPolicy")]
    public string AssumptionPolicy { get; set; } = string.Empty;

    [JsonPropertyName("analysis")]
    public McpAnalysisIntentView? Analysis { get; set; }

    [JsonPropertyName("publishing")]
    public McpPublishIntentView? Publishing { get; set; }

    [JsonPropertyName("provenance")]
    public McpGroundingProvenance Provenance { get; set; } = new();
}

internal sealed class McpAnalysisIntentView
{
    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("requestedOutputs")]
    public IReadOnlyList<string> RequestedOutputs { get; set; } = [];

    [JsonPropertyName("constraints")]
    public McpIntentConstraintsInput? Constraints { get; set; }

    [JsonPropertyName("inputs")]
    public IReadOnlyList<string> Inputs { get; set; } = [];

    [JsonPropertyName("assumptionPolicy")]
    public string AssumptionPolicy { get; set; } = string.Empty;
}

internal sealed class McpPublishIntentView
{
    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

    [JsonPropertyName("sourceKind")]
    public string SourceKind { get; set; } = string.Empty;

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("targetKind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

internal sealed class McpGroundingProvenance
{
    [JsonPropertyName("sources")]
    public IReadOnlyList<McpGroundingProvenanceSource> Sources { get; set; } = [];

    [JsonPropertyName("processDefinitions")]
    public IReadOnlyList<string> ProcessDefinitions { get; set; } = [];

    [JsonPropertyName("assumptions")]
    public IReadOnlyList<string> Assumptions { get; set; } = [];

    [JsonPropertyName("clarificationsAsked")]
    public IReadOnlyList<string> ClarificationsAsked { get; set; } = [];

    [JsonPropertyName("clarificationsAnswered")]
    public IReadOnlyList<string> ClarificationsAnswered { get; set; } = [];
}

internal sealed class McpGroundingProvenanceSource
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class McpCandidateRanking
{
    [JsonPropertyName("datasets")]
    public IReadOnlyList<McpGroundingCandidate> Datasets { get; set; } = [];

    [JsonPropertyName("processes")]
    public IReadOnlyList<McpGroundingCandidate> Processes { get; set; } = [];

    [JsonPropertyName("templates")]
    public IReadOnlyList<McpGroundingCandidate> Templates { get; set; } = [];

    [JsonPropertyName("styles")]
    public IReadOnlyList<McpGroundingCandidate> Styles { get; set; } = [];

    [JsonPropertyName("deployments")]
    public IReadOnlyList<McpGroundingCandidate> Deployments { get; set; } = [];
}

internal sealed class McpGroundingCandidate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("confidenceBand")]
    public string ConfidenceBand { get; set; } = string.Empty;

    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; set; } = [];
}

internal sealed class McpClarificationEnvelope
{
    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

    [JsonPropertyName("reasonCodes")]
    public IReadOnlyList<string> ReasonCodes { get; set; } = [];

    [JsonPropertyName("questions")]
    public IReadOnlyList<McpClarificationQuestionView> Questions { get; set; } = [];
}

internal sealed class McpClarificationQuestionView
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public IReadOnlyList<McpClarificationOptionView>? Options { get; set; }
}

internal sealed class McpClarificationOptionView
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}
