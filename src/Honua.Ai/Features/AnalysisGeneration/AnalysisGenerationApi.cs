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

public sealed record AnalysisGenerationValidationIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>"error" for structural failures, "warning" for deferred run/publish-time bindings.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";
}

/// <summary>HTTP request DTO for <c>POST /api/v1/analysis/content/generate</c> (mirrors the console request).</summary>
public sealed record GenerateAnalysisContentRequest
{
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

public sealed record AnalysisGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record AnalysisGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record AnalysisGenerationClarification
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<AnalysisGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record AnalysisGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record AnalysisGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

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
