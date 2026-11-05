// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Reporting.Models;

internal sealed class ReportingOpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public ReportingOpenAiMessage[] Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("response_format")]
    public ReportingOpenAiResponseFormat? ResponseFormat { get; set; }
}

internal sealed class ReportingOpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

internal sealed class ReportingOpenAiResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}

internal sealed class ReportingOpenAiChatResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("choices")]
    public ReportingOpenAiChoice[] Choices { get; set; } = [];
}

internal sealed class ReportingOpenAiChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ReportingOpenAiMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class NarrativeFillResponse
{
    [JsonPropertyName("slots")]
    public Dictionary<string, string> Slots { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class NarrativePromptPayload
{
    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonPropertyName("processId")]
    public string ProcessId { get; set; } = string.Empty;

    [JsonPropertyName("processFamily")]
    public string ProcessFamily { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("slots")]
    public List<NarrativePromptSlot> Slots { get; set; } = new();
}

internal sealed class NarrativePromptSlot
{
    [JsonPropertyName("slotId")]
    public string SlotId { get; set; } = string.Empty;

    [JsonPropertyName("hint")]
    public string? Hint { get; set; }

    [JsonPropertyName("deterministicText")]
    public string DeterministicText { get; set; } = string.Empty;
}
