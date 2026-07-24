// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Adapters.Models;

/// <summary>OpenAI-compatible <c>/chat/completions</c> request with <c>stream: true</c>.</summary>
internal sealed class OpenAiProxyRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public OpenAiProxyMessage[] Messages { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("tools")]
    public OpenAiProxyTool[]? Tools { get; set; }

    /// <summary>
    /// One of <c>"auto"</c>, <c>"none"</c>, <c>"required"</c>, or a
    /// <c>{"type":"function","function":{"name":...}}</c> object. Modeled as <see cref="JsonElement"/>
    /// because the shape varies by tool-choice mode; the adapter writes the right one directly.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("stream_options")]
    public OpenAiProxyStreamOptions? StreamOptions { get; set; }
}

internal sealed class OpenAiProxyMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

internal sealed class OpenAiProxyTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiProxyFunction Function { get; set; } = new();
}

internal sealed class OpenAiProxyFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

internal sealed class OpenAiProxyStreamOptions
{
    /// <summary>
    /// Requests a final usage-only chunk before <c>[DONE]</c> (OpenAI, OpenRouter, and most
    /// LiteLLM-fronted backends honor this; a backend that does not recognize the field ignores it).
    /// </summary>
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; } = true;
}

/// <summary>One decoded OpenAI-compatible streaming chunk (a <c>data:</c> line's JSON body, excluding the literal <c>[DONE]</c> sentinel).</summary>
internal sealed class OpenAiStreamChunk
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public OpenAiStreamChoice[]? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiStreamUsage? Usage { get; set; }
}

internal sealed class OpenAiStreamChoice
{
    [JsonPropertyName("delta")]
    public OpenAiStreamDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class OpenAiStreamDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public OpenAiStreamToolCallDelta[]? ToolCalls { get; set; }
}

internal sealed class OpenAiStreamToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public OpenAiStreamFunctionDelta? Function { get; set; }
}

internal sealed class OpenAiStreamFunctionDelta
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal sealed class OpenAiStreamUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}
