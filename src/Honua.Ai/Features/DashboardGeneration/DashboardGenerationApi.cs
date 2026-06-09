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

    /// <summary>Human-readable prompt presented to the operator.</summary>
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

/// <summary>A single prior conversation turn supplied to ground a dashboard-generation refine request.</summary>
public sealed record DashboardGenerationConversationTurn
{
    /// <summary>Role of the turn author (for example <c>user</c> or <c>assistant</c>).</summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>Natural-language content of the turn.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

/// <summary>An answer selecting one option for a previously emitted clarification question.</summary>
public sealed record DashboardGenerationAnswer
{
    /// <summary>Identifier of the clarification question being answered.</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    /// <summary>Identifier of the chosen option.</summary>
    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

/// <summary>A clarification question returned when the prompt is ambiguous and needs operator input.</summary>
public sealed record DashboardGenerationClarification
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
    public IReadOnlyList<DashboardGenerationClarificationChoice> Choices { get; init; } = [];
}

/// <summary>A selectable choice for a clarification question.</summary>
public sealed record DashboardGenerationClarificationChoice
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
public sealed record DashboardGenerationCapabilityState
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

/// <summary>Token and latency usage metrics for a dashboard-generation request.</summary>
public sealed record DashboardGenerationUsage
{
    /// <summary>Prompt tokens consumed, when reported by the provider.</summary>
    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens produced, when reported by the provider.</summary>
    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; init; }

    /// <summary>End-to-end latency in milliseconds, when measured.</summary>
    [JsonPropertyName("latencyMs")]
    public int? LatencyMs { get; init; }
}

/// <summary>Source-generated JSON context for the dashboard-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateDashboardContentRequest))]
[JsonSerializable(typeof(DashboardGenerationResult))]
public sealed partial class DashboardGenerationApiJsonContext : JsonSerializerContext;
