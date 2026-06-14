// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.AppGeneration;

/// <summary>
/// Raw structured output the model returns for an app-generation turn, mirroring
/// <c>MapGenerationModelProposal</c>. The provider deserializes the chat completion content into this
/// shape (constrained by <see cref="AppGenerationSchema"/>). Uses concrete array collections for safe
/// source-generated deserialization; the service maps these to the public
/// <see cref="AppGenerationResult"/> contract.
///
/// Unlike the map family — whose body deserializes into a typed <c>MapPackage</c> — the app package body
/// is the console's opaque <c>studio-app/v1</c> envelope (the console's <c>StudioAppPackageMapper</c>
/// produces/reads it), so it is carried as a raw <see cref="JsonElement"/> the console round-trips
/// directly. The server validates it structurally from the element (see
/// <see cref="AppGenerationStructuralValidator"/>) without a typed model.
/// </summary>
internal sealed class AppGenerationModelProposal
{
    /// <summary>generated | needs-clarification | unsupported | refused.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    /// <summary>The proposed studio-app/v1 package body; present when status is "generated".</summary>
    [JsonPropertyName("app")]
    public JsonElement? App { get; init; }

    /// <summary>One-paragraph rationale (or refusal reason).</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Clarification questions when the request is ambiguous.</summary>
    [JsonPropertyName("clarifications")]
    public AppGenerationModelClarification[] Clarifications { get; init => field = value ?? []; } = [];

    /// <summary>Requested pages/components that could not be mapped to a supported capability.</summary>
    [JsonPropertyName("unmappedRequests")]
    public string[] UnmappedRequests { get; init => field = value ?? []; } = [];

    /// <summary>Optional capability state when a requested capability is unsupported.</summary>
    [JsonPropertyName("capabilityState")]
    public AppGenerationModelCapabilityState? CapabilityState { get; init; }
}

internal sealed class AppGenerationModelClarification
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
    public AppGenerationModelChoice[] Choices { get; init => field = value ?? []; } = [];
}

internal sealed class AppGenerationModelChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

internal sealed class AppGenerationModelCapabilityState
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
/// disabled). The app body is an opaque <see cref="JsonElement"/>, so no string-enum converter is
/// needed here (unlike the map context, whose body carries a typed source-protocol enum).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppGenerationModelProposal))]
internal sealed partial class AppGenerationJsonContext : JsonSerializerContext;
