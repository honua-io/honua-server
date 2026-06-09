// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Grounds a natural-language prompt in the map-package vocabulary (source protocols, basemaps, style
/// fields), runs the configured generation provider, and applies
/// <see cref="MapGenerationStructuralValidator"/> as a generation-lenient gate (structural failures
/// only; layer/style/source binding is deferred to publish) with a bounded repair loop — so the client
/// never receives a structurally-invalid map. The map-family counterpart to the form generation service.
/// </summary>
public interface IMapGenerationService
{
    Task<MapGenerationResult> GenerateAsync(MapGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine a map from a prompt.</summary>
public sealed record MapGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public MapPackage? CurrentMap { get; init; }
    public IReadOnlyList<MapGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<MapGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract: status, package, rationale,
/// clarifications, validation, unmappedRequests, capabilityState, provider, model.
/// </summary>
public sealed record MapGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("package")]
    public MapPackage? Package { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<MapGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("validation")]
    public MapPackageValidationResult? Validation { get; init; }

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public MapGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>HTTP request DTO for <c>POST /api/v1/console/studio/map-packages/generate</c> (mirrors the console request).</summary>
public sealed record GenerateMapPackageRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("package")]
    public MapPackage? Package { get; init; }

    [JsonPropertyName("conversation")]
    public MapGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public MapGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

/// <summary>A single prior conversation turn supplied to ground a map-generation refine request.</summary>
public sealed record MapGenerationConversationTurn
{
    /// <summary>Role of the turn author (for example <c>user</c> or <c>assistant</c>).</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>Natural-language content of the turn.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

/// <summary>An answer selecting one option for a previously emitted clarification question.</summary>
public sealed record MapGenerationAnswer
{
    /// <summary>Identifier of the clarification question being answered.</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>Identifier of the chosen option.</summary>
    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

/// <summary>A clarification question returned when the prompt is ambiguous and needs operator input.</summary>
public sealed record MapGenerationClarification
{
    /// <summary>Stable identifier of the clarification question.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Clarification kind discriminator.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Human-readable prompt presented to the operator.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Optional explanation of why the clarification is required.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Selectable answer choices for the clarification.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<MapGenerationClarificationChoice> Choices { get; init; } = [];
}

/// <summary>A selectable choice for a <see cref="MapGenerationClarification"/>.</summary>
public sealed record MapGenerationClarificationChoice
{
    /// <summary>Stable identifier of the choice.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable label for the choice.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional description of the effect selecting this choice has on generation.</summary>
    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

/// <summary>Reports the availability state of a generation capability for the current request.</summary>
public sealed record MapGenerationCapabilityState
{
    /// <summary>Capability name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Capability state (for example <c>available</c> or <c>unavailable</c>).</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>Optional reason explaining the reported state.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Source-generated JSON context for the map-generation HTTP request/response DTOs.
/// <c>UseStringEnumConverter</c> so the nested <c>MapPackage</c> source protocol enum round-trips by
/// name (e.g. "ogc_features").
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GenerateMapPackageRequest))]
[JsonSerializable(typeof(MapGenerationResult))]
public sealed partial class MapGenerationApiJsonContext : JsonSerializerContext;
