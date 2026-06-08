// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Ai.QueryGeneration;

/// <summary>
/// Grounds a natural-language prompt in the saved-query (spatial/attribute filter) vocabulary, runs the
/// configured generation provider, and applies the structural <see cref="QueryGenerationValidationGate"/>
/// as a generation-lenient gate (filter-plan structure only — comparison/spatial/temporal operators
/// valid, spatial predicates well-formed; the target layer and its field schema are deferred to
/// run/preview) with a bounded repair loop — so the client never receives a structurally-invalid saved
/// query. The query-family counterpart to the form and analysis generation services.
/// </summary>
public interface IQueryGenerationService
{
    Task<QueryGenerationResult> GenerateAsync(QueryGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine a saved query from a prompt.</summary>
public sealed record QueryGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public SavedQueryContent? CurrentQuery { get; init; }
    public IReadOnlyList<QueryGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<QueryGenerationAnswer> Answers { get; init; } = [];

    /// <summary>
    /// Real published layers the caller knows about (catalog grounding). When the prompt matches one, the
    /// model binds its real numeric layerId and picks outFields from the real field list instead of using
    /// layerId 0 / guessing field names. Empty means "no catalog" — the model falls back to the placeholder.
    /// </summary>
    public IReadOnlyList<QueryGenerationSource> AvailableSources { get; init; } = [];
}

/// <summary>One real, published layer the query model may bind directly (catalog grounding).</summary>
public sealed record QueryGenerationSource
{
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    [JsonPropertyName("layerId")]
    public string LayerId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("fields")]
    public string[]? Fields { get; init; }
}

/// <summary>
/// Generation result. JSON property names match the console wire contract: status, query, rationale,
/// clarifications, validation, unmappedRequests, capabilityState, provider, model.
/// </summary>
public sealed record QueryGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("query")]
    public SavedQueryContent? Query { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<QueryGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("validation")]
    public QueryGenerationValidation? Validation { get; init; }

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public QueryGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>
/// Validation projection returned with a generated query: the structural failures that gated generation
/// (empty when the filter plan passed) and the deferred run/preview-time bindings (e.g. layer/field not
/// resolved) surfaced as non-blocking warnings, mirroring the form and analysis gates' deferred bindings.
/// </summary>
public sealed record QueryGenerationValidation
{
    [JsonPropertyName("issues")]
    public IReadOnlyList<QueryGenerationValidationIssue> Issues { get; init; } = [];
}

public sealed record QueryGenerationValidationIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>"error" for structural failures, "warning" for deferred run/preview-time bindings.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";
}

/// <summary>HTTP request DTO for <c>POST /api/v1/analysis/content/queries/generate</c> (mirrors the console request).</summary>
public sealed record GenerateSavedQueryRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("query")]
    public SavedQueryContent? Query { get; init; }

    [JsonPropertyName("conversation")]
    public QueryGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public QueryGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];

    /// <summary>Catalog grounding: real published layers the model may bind directly (see QueryGenerationSource).</summary>
    [JsonPropertyName("availableSources")]
    public QueryGenerationSource[] AvailableSources { get; init => field = value ?? []; } = [];
}

public sealed record QueryGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record QueryGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record QueryGenerationClarification
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
    public IReadOnlyList<QueryGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record QueryGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record QueryGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Source-generated JSON context for the query-generation HTTP request/response DTOs.
/// <c>UseStringEnumConverter</c> keeps the embedded <see cref="SavedQueryContent"/> /
/// <see cref="Honua.Core.Features.NlQuery.Domain.FilterPlan"/> enums on the same wire spellings the
/// saved-query content admin endpoints already accept.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GenerateSavedQueryRequest))]
[JsonSerializable(typeof(QueryGenerationResult))]
public sealed partial class QueryGenerationApiJsonContext : JsonSerializerContext;
