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

public sealed record AppGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record AppGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record AppGenerationClarification
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
    public IReadOnlyList<AppGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record AppGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record AppGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Source-generated JSON context for the app-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateAppPackageRequest))]
[JsonSerializable(typeof(AppGenerationResult))]
public sealed partial class AppGenerationApiJsonContext : JsonSerializerContext;
