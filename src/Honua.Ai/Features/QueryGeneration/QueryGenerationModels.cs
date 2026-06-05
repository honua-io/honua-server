// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Ai.QueryGeneration;

/// <summary>
/// Raw structured output the model returns for a query-generation turn, mirroring
/// <c>AnalysisGenerationModelProposal</c>/<c>FormGenerationModelProposal</c>. The provider deserializes
/// the chat completion content into this shape (constrained by <see cref="QueryGenerationSchema"/>).
/// Uses concrete array collections for safe source-generated deserialization; the service maps these to
/// the public <see cref="QueryGenerationResult"/> contract.
/// </summary>
internal sealed class QueryGenerationModelProposal
{
    /// <summary>generated | needs-clarification | unsupported | refused.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    /// <summary>The proposed saved-query content; present when status is "generated".</summary>
    [JsonPropertyName("query")]
    public SavedQueryContent? Query { get; init; }

    /// <summary>One-paragraph rationale (or refusal reason).</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Clarification questions when the request is ambiguous.</summary>
    [JsonPropertyName("clarifications")]
    public QueryGenerationModelClarification[] Clarifications { get; init => field = value ?? []; } = [];

    /// <summary>Requested filters/predicates that could not be mapped to a supported operator.</summary>
    [JsonPropertyName("unmappedRequests")]
    public string[] UnmappedRequests { get; init => field = value ?? []; } = [];

    /// <summary>Optional capability state when a requested capability is unsupported.</summary>
    [JsonPropertyName("capabilityState")]
    public QueryGenerationModelCapabilityState? CapabilityState { get; init; }
}

internal sealed class QueryGenerationModelClarification
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
    public QueryGenerationModelChoice[] Choices { get; init => field = value ?? []; } = [];
}

internal sealed class QueryGenerationModelChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

internal sealed class QueryGenerationModelCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Source-generated JSON context for deserializing the model proposal (reflection serialization is
/// disabled). <c>UseStringEnumConverter</c> keeps the embedded <see cref="SavedQueryContent"/> /
/// <see cref="Honua.Core.Features.NlQuery.Domain.FilterPlan"/> enums (the <c>FilterPlanCombinator</c>
/// and/or wire spellings) on the same shape the saved-query content contract already accepts.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(QueryGenerationModelProposal))]
internal sealed partial class QueryGenerationJsonContext : JsonSerializerContext;
