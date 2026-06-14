// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Ai.AnalysisGeneration;

/// <summary>
/// Grounds a natural-language prompt in the geoprocessing analysis-method vocabulary (the process
/// catalog), runs the configured generation provider, and applies the structural
/// <c>ProcessPlanValidator</c> as a generation-lenient gate (method/parameter structure only; input
/// layer existence is deferred to run/publish) with a bounded repair loop — so the client never
/// receives a structurally-invalid analysis package. The analysis-family counterpart to the form and
/// workflow generation services.
/// </summary>
public interface IAnalysisGenerationService
{
    Task<AnalysisGenerationResult> GenerateAsync(AnalysisGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine an analysis package from a prompt.</summary>
public sealed record AnalysisGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public AnalysisPackageContent? CurrentAnalysis { get; init; }
    public IReadOnlyList<AnalysisGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<AnalysisGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract: status, analysis,
/// rationale, clarifications, validation, unmappedRequests, capabilityState, provider, model.
/// </summary>
public sealed record AnalysisGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("analysis")]
    public AnalysisPackageContent? Analysis { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<AnalysisGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("validation")]
    public AnalysisGenerationValidation? Validation { get; init; }

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public AnalysisGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>
/// Validation projection returned with a generated analysis: the structural failures that gated
/// generation (empty when the package passed) and the deferred run/publish-time bindings (e.g. input
/// layer not found) surfaced as non-blocking warnings, mirroring the form gate's deferred bindings.
/// </summary>
public sealed record AnalysisGenerationValidation
{
    [JsonPropertyName("issues")]
    public IReadOnlyList<AnalysisGenerationValidationIssue> Issues { get; init; } = [];
}

/// <summary>A single structural validation finding for a generated artifact.</summary>
public sealed record AnalysisGenerationValidationIssue
{
    /// <summary>Stable machine-readable issue code.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>Human-readable description of the issue.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional path to the offending element within the artifact.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>"error" for structural failures, "warning" for deferred run/publish-time bindings.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";
}

/// <summary>HTTP request DTO for <c>POST /api/v1/analysis/content/generate</c> (mirrors the console request).</summary>
public sealed record GenerateAnalysisContentRequest
{
    /// <summary>Human-readable prompt presented to the operator.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("analysis")]
    public AnalysisPackageContent? Analysis { get; init; }

    [JsonPropertyName("conversation")]
    public AnalysisGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public AnalysisGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

/// <summary>A single prior conversation turn supplied to ground a analysis-generation refine request.</summary>
public sealed record AnalysisGenerationConversationTurn
{
    /// <summary>Role of the turn author (for example <c>user</c> or <c>assistant</c>).</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>Natural-language content of the turn.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

/// <summary>An answer selecting one option for a previously emitted clarification question.</summary>
public sealed record AnalysisGenerationAnswer
{
    /// <summary>Identifier of the clarification question being answered.</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>Identifier of the chosen option.</summary>
    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

/// <summary>A clarification question returned when the prompt is ambiguous and needs operator input.</summary>
public sealed record AnalysisGenerationClarification
{
    /// <summary>Stable identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Discriminator value.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Human-readable prompt presented to the operator.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Optional explanatory reason.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Selectable answer choices for the clarification.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<AnalysisGenerationClarificationChoice> Choices { get; init; } = [];
}

/// <summary>A selectable choice for a clarification question.</summary>
public sealed record AnalysisGenerationClarificationChoice
{
    /// <summary>Stable identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable label.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional description of the effect selecting this choice has on generation.</summary>
    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

/// <summary>Reports the availability state of a generation capability for the current request.</summary>
public sealed record AnalysisGenerationCapabilityState
{
    /// <summary>Capability name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Capability state (for example <c>available</c> or <c>unavailable</c>).</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>Optional explanatory reason.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Source-generated JSON context for the analysis-generation HTTP request/response DTOs.
/// <c>UseStringEnumConverter</c> keeps the embedded <see cref="AnalysisPackageContent"/> enums on
/// their PascalCase member-name wire spellings, matching the analysis-content admin endpoints.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GenerateAnalysisContentRequest))]
[JsonSerializable(typeof(AnalysisGenerationResult))]
public sealed partial class AnalysisGenerationApiJsonContext : JsonSerializerContext;
