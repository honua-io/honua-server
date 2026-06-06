// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Publishing.Dashboards;

namespace Honua.Ai.DashboardGeneration;

/// <summary>
/// Raw structured output the model returns for a dashboard-generation turn, mirroring
/// <c>ReportGenerationModelProposal</c>. The provider deserializes the chat completion content into this
/// shape (constrained by <see cref="DashboardGenerationSchema"/>). Uses concrete array collections for safe
/// source-generated deserialization; the service maps these to the public
/// <see cref="DashboardGenerationResult"/>.
/// </summary>
internal sealed class DashboardGenerationModelProposal
{
    /// <summary>generated | needs-clarification | unsupported | refused.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    /// <summary>The proposed dashboard document; present when status is "generated".</summary>
    [JsonPropertyName("dashboard")]
    public DashboardDocument? Dashboard { get; init; }

    /// <summary>Suggested route slug for the proposed dashboard.</summary>
    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    /// <summary>One-paragraph rationale (or refusal reason).</summary>
    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    /// <summary>Clarification questions when the request is ambiguous.</summary>
    [JsonPropertyName("clarifications")]
    public DashboardGenerationModelClarification[] Clarifications { get; init => field = value ?? []; } = [];

    /// <summary>Requested panels/charts that could not be mapped to a supported kind/binding.</summary>
    [JsonPropertyName("unmappedRequests")]
    public string[] UnmappedRequests { get; init => field = value ?? []; } = [];

    /// <summary>Optional capability state when a requested capability is unsupported.</summary>
    [JsonPropertyName("capabilityState")]
    public DashboardGenerationModelCapabilityState? CapabilityState { get; init; }
}

internal sealed class DashboardGenerationModelClarification
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
    public DashboardGenerationModelChoice[] Choices { get; init => field = value ?? []; } = [];
}

internal sealed class DashboardGenerationModelChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

internal sealed class DashboardGenerationModelCapabilityState
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
[JsonSerializable(typeof(DashboardGenerationModelProposal))]
internal sealed partial class DashboardGenerationJsonContext : JsonSerializerContext;
