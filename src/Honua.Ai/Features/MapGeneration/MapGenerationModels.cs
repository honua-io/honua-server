// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.MapGeneration;

/// <summary>
/// Raw structured output the model returns for a map-generation turn, mirroring
/// <c>FormGenerationModelProposal</c>. The provider deserializes the chat completion content into this
/// shape (constrained by <see cref="MapGenerationSchema"/>). Uses concrete array collections for safe
/// source-generated deserialization; the service maps these to the public
/// <see cref="MapGenerationResult"/> contract.
/// </summary>
internal sealed class MapGenerationModelProposal
{
    /// <summary>generated | needs-clarification | unsupported | refused.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    /// <summary>The proposed map package body; present when status is "generated".</summary>
    [JsonPropertyName("map")]
    public MapPackage? Map { get; init; }

    /// <summary>One-paragraph rationale (or refusal reason).</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Clarification questions when the request is ambiguous.</summary>
    [JsonPropertyName("clarifications")]
    public MapGenerationModelClarification[] Clarifications { get; init => field = value ?? []; } = [];

    /// <summary>Requested layers/styling that could not be mapped to a supported capability.</summary>
    [JsonPropertyName("unmappedRequests")]
    public string[] UnmappedRequests { get; init => field = value ?? []; } = [];

    /// <summary>Optional capability state when a requested capability is unsupported.</summary>
    [JsonPropertyName("capabilityState")]
    public MapGenerationModelCapabilityState? CapabilityState { get; init; }
}

internal sealed class MapGenerationModelClarification
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
    public MapGenerationModelChoice[] Choices { get; init => field = value ?? []; } = [];
}

internal sealed class MapGenerationModelChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

internal sealed class MapGenerationModelCapabilityState
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
/// disabled). <c>UseStringEnumConverter</c> so the map body's <c>protocol</c> token deserializes into
/// <see cref="Honua.Core.Features.Geoprocessing.Domain.SourceProtocol"/> via its
/// <c>JsonStringEnumMemberName</c> names (e.g. "ogc_features").
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(MapGenerationModelProposal))]
internal sealed partial class MapGenerationJsonContext : JsonSerializerContext;
