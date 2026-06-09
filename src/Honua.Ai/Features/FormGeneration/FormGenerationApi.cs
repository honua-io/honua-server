// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Grounds a natural-language prompt in the form field-type vocabulary, runs the configured
/// generation provider, and applies <see cref="FormPackageValidator"/> as a generation-lenient
/// gate (structural failures only; target binding is deferred to publish) with a bounded repair
/// loop — so the client never receives a structurally-invalid form. The form-family counterpart to
/// the workflow generation service.
/// </summary>
public interface IFormGenerationService
{
    Task<FormGenerationResult> GenerateAsync(FormGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine a form from a prompt.</summary>
public sealed record FormGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public FormPackageDocument? CurrentForm { get; init; }
    public IReadOnlyList<FormGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<FormGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract
/// (<c>HonuaFormGenerationResult</c>): status, package, rationale, clarifications, validation,
/// unmappedRequests, capabilityState, provider, model.
/// </summary>
public sealed record FormGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("package")]
    public FormPackageDocument? Package { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<FormGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("validation")]
    public FormPackageValidationResult? Validation { get; init; }

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public FormGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>HTTP request DTO for <c>POST /api/v1/admin/forms/packages/generate</c> (mirrors the console <c>GenerateFormPackageRequest</c>).</summary>
public sealed record GenerateFormPackageRequest
{
    /// <summary>Human-readable prompt presented to the operator.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("package")]
    public FormPackageDocument? Package { get; init; }

    [JsonPropertyName("conversation")]
    public FormGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public FormGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

/// <summary>A single prior conversation turn supplied to ground a form-generation refine request.</summary>
public sealed record FormGenerationConversationTurn
{
    /// <summary>Role of the turn author (for example <c>user</c> or <c>assistant</c>).</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>Natural-language content of the turn.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

/// <summary>An answer selecting one option for a previously emitted clarification question.</summary>
public sealed record FormGenerationAnswer
{
    /// <summary>Identifier of the clarification question being answered.</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>Identifier of the chosen option.</summary>
    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

/// <summary>A clarification question returned when the prompt is ambiguous and needs operator input.</summary>
public sealed record FormGenerationClarification
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
    public IReadOnlyList<FormGenerationClarificationChoice> Choices { get; init; } = [];
}

/// <summary>A selectable choice for a clarification question.</summary>
public sealed record FormGenerationClarificationChoice
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
public sealed record FormGenerationCapabilityState
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

/// <summary>Source-generated JSON context for the form-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateFormPackageRequest))]
[JsonSerializable(typeof(FormGenerationResult))]
public sealed partial class FormGenerationApiJsonContext : JsonSerializerContext;
