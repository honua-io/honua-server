// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>
/// Structured detail JSON stored in <c>AuditEvent.Details</c> for one proxied chat call
/// (honua-server#3000 REQ-002: "audit records for every call"). The audit row itself already
/// carries actor, timestamp, resource id (provider name), and outcome; this only adds the fields an
/// investigator would otherwise have to reconstruct from logs — kind, model, token counts, latency,
/// stop reason. Never includes prompt/response content or credentials.
/// </summary>
public sealed class StudioAiProxyAuditDetails
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("stopReason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}
