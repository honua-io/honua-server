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

public sealed record MapGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record MapGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record MapGenerationClarification
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
    public IReadOnlyList<MapGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record MapGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record MapGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

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
