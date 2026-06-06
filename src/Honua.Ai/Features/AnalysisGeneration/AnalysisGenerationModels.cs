// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.AnalysisContent.Domain;

namespace Honua.Ai.AnalysisGeneration;

/// <summary>
/// Raw structured output the model returns for an analysis-generation turn, mirroring
/// <c>FormGenerationModelProposal</c>. The provider deserializes the chat completion content
/// into this shape (constrained by <see cref="AnalysisGenerationSchema"/>). Uses concrete array
/// collections for safe source-generated deserialization; the service maps these to the public
/// <see cref="AnalysisGenerationResult"/> contract.
/// </summary>
internal sealed class AnalysisGenerationModelProposal
{
    /// <summary>generated | needs-clarification | unsupported | refused.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    /// <summary>The proposed analysis-package content; present when status is "generated".</summary>
    [JsonPropertyName("analysis")]
    public AnalysisPackageContent? Analysis { get; init; }

    /// <summary>One-paragraph rationale (or refusal reason).</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Clarification questions when the request is ambiguous.</summary>
    [JsonPropertyName("clarifications")]
    public AnalysisGenerationModelClarification[] Clarifications { get; init => field = value ?? []; } = [];

    /// <summary>Requested analysis steps/methods that could not be mapped to a supported process.</summary>
    [JsonPropertyName("unmappedRequests")]
    public string[] UnmappedRequests { get; init => field = value ?? []; } = [];

    /// <summary>Optional capability state when a requested capability is unsupported.</summary>
    [JsonPropertyName("capabilityState")]
    public AnalysisGenerationModelCapabilityState? CapabilityState { get; init; }
}

internal sealed class AnalysisGenerationModelClarification
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
    public AnalysisGenerationModelChoice[] Choices { get; init => field = value ?? []; } = [];
}

internal sealed class AnalysisGenerationModelChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

internal sealed class AnalysisGenerationModelCapabilityState
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
/// disabled). <c>UseStringEnumConverter</c> keeps the <see cref="AnalysisPackageContent"/> enums
/// (<c>AnalysisPlanStepKind</c>, <c>ArtifactKind</c>) on their PascalCase member-name wire spellings —
/// the same shape the analysis-content admin endpoints already accept.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AnalysisGenerationModelProposal))]
internal sealed partial class AnalysisGenerationJsonContext : JsonSerializerContext;
