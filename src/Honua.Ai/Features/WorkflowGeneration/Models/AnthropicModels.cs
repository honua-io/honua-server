// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.WorkflowGeneration.Models;

/// <summary>
/// Anthropic Messages API request. Structured output is obtained via a single forced tool:
/// the model must call the <c>emit_workflow</c> tool whose <c>input_schema</c> is the
/// constrained workflow proposal schema.
/// </summary>
internal sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("messages")]
    public AnthropicMessage[] Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public AnthropicTool[]? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public AnthropicToolChoice? ToolChoice { get; set; }
}

/// <summary>An Anthropic message.</summary>
internal sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>An Anthropic tool definition used to constrain structured output.</summary>
internal sealed class AnthropicTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public JsonElement? InputSchema { get; set; }
}

/// <summary>Forces the model to call a specific tool.</summary>
internal sealed class AnthropicToolChoice
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "tool";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Anthropic Messages API response.</summary>
internal sealed class AnthropicMessagesResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("content")]
    public AnthropicContentBlock[] Content { get; set; } = [];

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>A content block in an Anthropic response (text or tool_use).</summary>
internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }
}

/// <summary>Anthropic token usage.</summary>
internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}
