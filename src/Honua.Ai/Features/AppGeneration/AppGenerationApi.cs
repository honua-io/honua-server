// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Grounds a natural-language prompt in the studio-app/v1 vocabulary (component kinds, permission tiers,
/// visibility tiers), runs the configured generation provider, and applies
/// <see cref="AppGenerationStructuralValidator"/> as a generation-lenient gate (structural failures only;
/// content binding is deferred to publish) with a bounded repair loop — so the client never receives a
/// structurally-invalid app. The app-family counterpart to the map generation service.
/// </summary>
public interface IAppGenerationService
{
    Task<AppGenerationResult> GenerateAsync(AppGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine an app from a prompt.</summary>
public sealed record AppGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }

    /// <summary>Current studio-app/v1 app body for a refine turn (raw payload from the console), or null for fresh generation.</summary>
    public JsonElement? CurrentApp { get; init; }
    public IReadOnlyList<AppGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<AppGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract: status, package, rationale,
/// clarifications, validation, unmappedRequests, capabilityState, provider, model. The package is the
/// opaque <c>studio-app/v1</c> body the console round-trips through its own StudioAppPackageMapper.
/// </summary>
public sealed record AppGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("package")]
    public JsonElement? Package { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<AppGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("validation")]
    public AppPackageValidationResult? Validation { get; init; }

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public AppGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>HTTP request DTO for <c>POST /api/v1/studio/app-packages/generate</c> (mirrors the console request).</summary>
public sealed record GenerateAppPackageRequest
{
    /// <summary>Human-readable prompt presented to the operator.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Current studio-app/v1 app body for a REFINE turn; null requests fresh generation.</summary>
    [JsonPropertyName("package")]
    public JsonElement? Package { get; init; }

    [JsonPropertyName("conversation")]
    public AppGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public AppGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

/// <summary>A single prior conversation turn supplied to ground a app-generation refine request.</summary>
public sealed record AppGenerationConversationTurn
{
    /// <summary>Role of the turn author (for example <c>user</c> or <c>assistant</c>).</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>Natural-language content of the turn.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

/// <summary>An answer selecting one option for a previously emitted clarification question.</summary>
public sealed record AppGenerationAnswer
{
    /// <summary>Identifier of the clarification question being answered.</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>Identifier of the chosen option.</summary>
    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

/// <summary>A clarification question returned when the prompt is ambiguous and needs operator input.</summary>
public sealed record AppGenerationClarification
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
    public IReadOnlyList<AppGenerationClarificationChoice> Choices { get; init; } = [];
}

/// <summary>A selectable choice for a clarification question.</summary>
public sealed record AppGenerationClarificationChoice
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
public sealed record AppGenerationCapabilityState
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

/// <summary>Source-generated JSON context for the app-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateAppPackageRequest))]
[JsonSerializable(typeof(AppGenerationResult))]
public sealed partial class AppGenerationApiJsonContext : JsonSerializerContext;
