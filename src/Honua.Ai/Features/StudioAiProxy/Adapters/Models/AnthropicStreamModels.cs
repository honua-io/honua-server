// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Adapters.Models;

internal sealed class AnthropicProxyRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("messages")]
    public AnthropicProxyMessage[] Messages { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("tools")]
    public AnthropicProxyTool[]? Tools { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("tool_choice")]
    public AnthropicProxyToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;
}

internal sealed class AnthropicProxyMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
}

internal sealed class AnthropicProxyTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement InputSchema { get; set; }
}

internal sealed class AnthropicProxyToolChoice
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "auto";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// Only data lines are parsed because the payload type field already carries the same discriminator as the event line.
internal sealed class AnthropicStreamFrame
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("message")]
    public AnthropicStreamMessage? Message { get; set; }

    [JsonPropertyName("content_block")]
    public AnthropicStreamContentBlock? ContentBlock { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicStreamDelta? Delta { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicStreamUsage? Usage { get; set; }
}

internal sealed class AnthropicStreamMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicStreamUsage? Usage { get; set; }
}

internal sealed class AnthropicStreamContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class AnthropicStreamDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}

internal sealed class AnthropicStreamUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }
}
