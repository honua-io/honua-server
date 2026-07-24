// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>
/// Role of a <see cref="StudioAiMessage"/> in the provider-neutral chat contract. Mirrors the
/// small role vocabulary shared by the Anthropic Messages API, OpenAI-compatible chat-completions
/// API, and Bedrock's Converse API so a single request shape can be translated to any of them.
/// </summary>
public enum StudioAiRole
{
    /// <summary>System / developer instructions. Carried out-of-band by some providers.</summary>
    System,

    /// <summary>A user turn.</summary>
    User,

    /// <summary>A model (assistant) turn, including prior tool calls the model made.</summary>
    Assistant,

    /// <summary>The result of a tool call, addressed back to the model by <see cref="StudioAiMessage.ToolCallId"/>.</summary>
    Tool
}

/// <summary>
/// One turn of provider-neutral chat history. <see cref="StudioAiRole.Tool"/> messages carry the
/// result of a previously requested tool call so the model can continue the turn.
/// </summary>
public sealed class StudioAiMessage
{
    /// <summary>The speaker of this turn.</summary>
    public required StudioAiRole Role { get; init; }

    /// <summary>Plain-text content of the turn. Never null; empty for a pure tool-result placeholder.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>For <see cref="StudioAiRole.Tool"/> messages: the id of the tool call this responds to.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For <see cref="StudioAiRole.Tool"/> messages: the name of the tool that was called.</summary>
    public string? ToolName { get; init; }
}

/// <summary>
/// A tool (function) the model may call, described by a JSON Schema input shape. Shared verbatim
/// across adapters — each adapter translates it into its own wire format (Anthropic
/// <c>input_schema</c>, OpenAI <c>function.parameters</c>, Bedrock <c>ToolSpecification</c>).
/// </summary>
public sealed class StudioAiToolDefinition
{
    /// <summary>Stable tool name the model references in a tool call.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description shown to the model.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema describing the tool's call arguments.</summary>
    public JsonElement InputSchema { get; init; }
}

/// <summary>How strongly the caller wants the model to invoke a tool.</summary>
public enum StudioAiToolChoiceMode
{
    /// <summary>Let the model decide whether to call a tool (provider default).</summary>
    Auto,

    /// <summary>Forbid tool calls for this turn (tools, if any, are not sent to the provider).</summary>
    None,

    /// <summary>Force some tool call, any of the declared tools.</summary>
    Required,

    /// <summary>Force a specific named tool call. <see cref="StudioAiToolChoice.ToolName"/> must be set.</summary>
    Specific
}

/// <summary>Tool-choice directive accompanying a <see cref="StudioAiChatRequest"/>.</summary>
public sealed class StudioAiToolChoice
{
    /// <summary>The requested tool-choice strength.</summary>
    public StudioAiToolChoiceMode Mode { get; init; } = StudioAiToolChoiceMode.Auto;

    /// <summary>Required when <see cref="Mode"/> is <see cref="StudioAiToolChoiceMode.Specific"/>.</summary>
    public string? ToolName { get; init; }
}

/// <summary>
/// Provider-neutral chat request. This is the one shape every adapter (Anthropic, OpenAI-compatible,
/// Bedrock) accepts; it is deliberately close to the "chat completions" shape the wider ecosystem has
/// converged on so translating it into any one provider's wire format is a small, mostly mechanical
/// mapping rather than a redesign per provider.
/// </summary>
public sealed class StudioAiChatRequest
{
    /// <summary>
    /// Name of the configured provider to route to (a key under <c>StudioAiProxy:Providers</c>).
    /// When omitted, the configured default provider is used.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>Optional model override; when omitted the provider's configured model is used.</summary>
    public string? Model { get; init; }

    /// <summary>Optional system/developer prompt, carried out-of-band from <see cref="Messages"/>.</summary>
    public string? System { get; init; }

    /// <summary>Conversation turns, oldest first.</summary>
    public required IReadOnlyList<StudioAiMessage> Messages { get; init; }

    /// <summary>Tools the model may call this turn, or null/empty for a tool-free turn.</summary>
    public IReadOnlyList<StudioAiToolDefinition>? Tools { get; init; }

    /// <summary>Tool-choice directive; ignored when <see cref="Tools"/> is empty.</summary>
    public StudioAiToolChoice? ToolChoice { get; init; }

    /// <summary>Maximum tokens to generate; falls back to the provider's configured default.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Sampling temperature; falls back to the provider's default when omitted.</summary>
    public double? Temperature { get; init; }
}

/// <summary>Wire (endpoint) request body for <c>POST /api/v1/studio/ai/chat</c>.</summary>
public sealed class StudioAiChatHttpRequest
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public List<StudioAiChatHttpMessage> Messages { get; init; } = [];

    [JsonPropertyName("tools")]
    public List<StudioAiChatHttpTool>? Tools { get; init; }

    [JsonPropertyName("toolChoice")]
    public StudioAiChatHttpToolChoice? ToolChoice { get; init; }

    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }
}

/// <summary>Wire shape of a <see cref="StudioAiMessage"/>. <see cref="Role"/> is lower-case (<c>"user"</c>, <c>"assistant"</c>, <c>"system"</c>, <c>"tool"</c>).</summary>
public sealed class StudioAiChatHttpMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }
}

/// <summary>Wire shape of a <see cref="StudioAiToolDefinition"/>.</summary>
public sealed class StudioAiChatHttpTool
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; init; }
}

/// <summary>Wire shape of a <see cref="StudioAiToolChoice"/>. <see cref="Mode"/> is lower-case (<c>"auto"</c>, <c>"none"</c>, <c>"required"</c>, <c>"specific"</c>).</summary>
public sealed class StudioAiChatHttpToolChoice
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "auto";

    [JsonPropertyName("toolName")]
    public string? ToolName { get; init; }
}
