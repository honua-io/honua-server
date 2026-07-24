// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Adapters.Models;

/// <summary>
/// Anthropic Messages API request with <c>stream: true</c>. Distinct from
/// <c>Honua.Ai.WorkflowGeneration.Models.AnthropicMessagesRequest</c> (which always forces a single
/// tool) — this proxy supports tool-free turns, multiple tools, and any tool-choice mode.
/// </summary>
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

/// <summary>
/// One decoded Anthropic SSE <c>data:</c> frame. Anthropic's streaming protocol sends a matching
/// <c>event: &lt;type&gt;</c> line before each <c>data:</c> line, but the payload's own
/// <see cref="Type"/> field already carries the same discriminator, so the adapter only needs to
/// parse <c>data:</c> lines. All fields below are null except the ones the given <see cref="Type"/>
/// populates — see https://docs.anthropic.com/en/api/messages-streaming for the frame shapes.
/// </summary>
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

/// <summary>A <c>content_block_delta</c> / <c>message_delta</c> payload's <c>delta</c> object.</summary>
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
