// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Publishing.Reports;

namespace Honua.Ai.ReportGeneration;

/// <summary>
/// Grounds a natural-language prompt in the report panel/chart vocabulary (Vega-Lite), runs the configured
/// generation provider, and applies <see cref="ReportDocumentValidator"/> as a structural generation gate
/// with a bounded repair loop — so the client never receives a structurally-invalid report. The
/// report-family counterpart to <c>IFormGenerationService</c>.
/// </summary>
public interface IReportGenerationService
{
    Task<ReportGenerationResult> GenerateAsync(ReportGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine a report from a prompt.</summary>
public sealed record ReportGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }

    /// <summary>Current report document for a refine turn (raw payload from the console), or null for fresh generation.</summary>
    public JsonElement? CurrentDocument { get; init; }
    public IReadOnlyList<ReportGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<ReportGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract
/// (<c>HonuaReportGenerationResult</c>): status, document, routeSlug, rationale, clarifications,
/// unmappedRequests, capabilityState, provider, model, usage. The document is an opaque JSON element (the
/// canonical <c>honua.report-document.v1</c> payload) so the console round-trips it through its own
/// report-document mapper.
/// </summary>
public sealed record ReportGenerationResult
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
    public IReadOnlyList<ReportGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public ReportGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("usage")]
    public ReportGenerationUsage? Usage { get; init; }
}

/// <summary>HTTP request DTO for <c>POST /api/v1/console/publications/generate</c> (mirrors the console <c>GenerateReportContentRequest</c>).</summary>
public sealed record GenerateReportContentRequest
{
    /// <summary>Always "report" for the report builder; the endpoint is shared with other content kinds.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "report";

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Current report-document payload for a REFINE turn; null requests fresh generation.</summary>
    [JsonPropertyName("document")]
    public JsonElement? Document { get; init; }

    [JsonPropertyName("conversation")]
    public ReportGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public ReportGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

public sealed record ReportGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record ReportGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record ReportGenerationClarification
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
    public IReadOnlyList<ReportGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record ReportGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record ReportGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record ReportGenerationUsage
{
    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("latencyMs")]
    public int? LatencyMs { get; init; }
}

/// <summary>Source-generated JSON context for the report-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateReportContentRequest))]
[JsonSerializable(typeof(ReportGenerationResult))]
public sealed partial class ReportGenerationApiJsonContext : JsonSerializerContext;
