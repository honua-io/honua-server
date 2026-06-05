// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.DashboardGeneration;

/// <summary>
/// Grounds a natural-language prompt in the dashboard panel/chart vocabulary (Vega-Lite charts, map/table/
/// filter/metric slots), runs the configured generation provider, and applies
/// <c>DashboardDocumentValidator</c> as a structural generation gate with a bounded repair loop — so the
/// client never receives a structurally-invalid dashboard. The dashboard-family counterpart to
/// <c>IReportGenerationService</c>.
/// </summary>
public interface IDashboardGenerationService
{
    Task<DashboardGenerationResult> GenerateAsync(DashboardGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine a dashboard from a prompt.</summary>
public sealed record DashboardGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }

    /// <summary>Current dashboard document for a refine turn (raw payload from the console), or null for fresh generation.</summary>
    public JsonElement? CurrentDocument { get; init; }
    public IReadOnlyList<DashboardGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<DashboardGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract: status, document, routeSlug,
/// rationale, clarifications, unmappedRequests, capabilityState, provider, model, usage. The document is an
/// opaque JSON element (the canonical <c>honua.dashboard-document.v1</c> payload) so the console
/// round-trips it through its own dashboard-document mapper.
/// </summary>
public sealed record DashboardGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("document")]
    public JsonElement? Document { get; init; }

    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<DashboardGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public DashboardGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("usage")]
    public DashboardGenerationUsage? Usage { get; init; }
}

/// <summary>HTTP request DTO for <c>POST /api/v1/console/publications/generate</c> with <c>kind=dashboard</c> (mirrors the console <c>GenerateDashboardContentRequest</c>).</summary>
public sealed record GenerateDashboardContentRequest
{
    /// <summary>Always "dashboard" for the dashboard builder; the endpoint is shared with other content kinds.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "dashboard";

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Current dashboard-document payload for a REFINE turn; null requests fresh generation.</summary>
    [JsonPropertyName("document")]
    public JsonElement? Document { get; init; }

    [JsonPropertyName("conversation")]
    public DashboardGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public DashboardGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

public sealed record DashboardGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record DashboardGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record DashboardGenerationClarification
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
    public IReadOnlyList<DashboardGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record DashboardGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record DashboardGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record DashboardGenerationUsage
{
    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("latencyMs")]
    public int? LatencyMs { get; init; }
}

/// <summary>Source-generated JSON context for the dashboard-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateDashboardContentRequest))]
[JsonSerializable(typeof(DashboardGenerationResult))]
public sealed partial class DashboardGenerationApiJsonContext : JsonSerializerContext;
