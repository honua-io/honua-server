// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Raw structured output the model returns for a form-generation turn, mirroring
/// <c>WorkflowGenerationModelProposal</c>. The provider deserializes the chat completion content
/// into this shape (constrained by <see cref="FormGenerationSchema"/>). Uses concrete array
/// collections for safe source-generated deserialization; the service maps these to the public
/// <see cref="FormGenerationResult"/> contract.
/// </summary>
internal sealed class FormGenerationModelProposal
{
    /// <summary>generated | needs-clarification | unsupported | refused.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    /// <summary>The proposed form package document; present when status is "generated".</summary>
    [JsonPropertyName("form")]
    public FormPackageDocument? Form { get; init; }

    /// <summary>One-paragraph rationale (or refusal reason).</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Clarification questions when the request is ambiguous.</summary>
    [JsonPropertyName("clarifications")]
    public FormGenerationModelClarification[] Clarifications { get; init => field = value ?? []; } = [];

    /// <summary>Requested fields/sections that could not be mapped to a supported type.</summary>
    [JsonPropertyName("unmappedRequests")]
    public string[] UnmappedRequests { get; init => field = value ?? []; } = [];

    /// <summary>Optional capability state when a requested capability is unsupported.</summary>
    [JsonPropertyName("capabilityState")]
    public FormGenerationModelCapabilityState? CapabilityState { get; init; }
}

internal sealed class FormGenerationModelClarification
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
    public FormGenerationModelChoice[] Choices { get; init => field = value ?? []; } = [];
}

internal sealed class FormGenerationModelChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

internal sealed class FormGenerationModelCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Source-generated JSON context for deserializing the model proposal (reflection serialization is disabled).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FormGenerationModelProposal))]
internal sealed partial class FormGenerationJsonContext : JsonSerializerContext;
