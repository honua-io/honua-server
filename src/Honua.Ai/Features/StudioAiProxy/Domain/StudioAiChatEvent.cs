// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>
/// The kind of a streamed <see cref="StudioAiChatEvent"/>. Deliberately a small, provider-neutral
/// vocabulary that every adapter (Anthropic, OpenAI-compatible, Bedrock) can be translated into and
/// out of without loss for the subset of behavior this proxy exposes (v0: text + tool calls).
/// </summary>
public enum StudioAiChatEventType
{
    /// <summary>The turn has started; no content yet.</summary>
    MessageStart,

    /// <summary>An incremental fragment of assistant text.</summary>
    TextDelta,

    /// <summary>A tool call has begun (id + name known; arguments not yet complete).</summary>
    ToolCallStart,

    /// <summary>An incremental fragment of a tool call's JSON arguments.</summary>
    ToolCallDelta,

    /// <summary>A tool call is complete; <see cref="StudioAiChatEvent.ToolArguments"/> carries the full parsed arguments when the adapter could assemble them.</summary>
    ToolCallStop,

    /// <summary>The turn has ended. Carries the stop reason and, when known, usage totals.</summary>
    MessageStop,

    /// <summary>A provider- or proxy-side failure ended the call before <see cref="MessageStop"/> could be produced normally.</summary>
    Error
}

/// <summary>
/// Why a turn stopped generating. Provider-neutral mapping of Anthropic <c>stop_reason</c>,
/// OpenAI-compatible <c>finish_reason</c>, and Bedrock Converse <c>StopReason</c>.
/// </summary>
public enum StudioAiStopReason
{
    /// <summary>The model completed its turn normally.</summary>
    EndTurn,

    /// <summary>The model stopped to make one or more tool calls.</summary>
    ToolCall,

    /// <summary>Generation was truncated at the configured token budget.</summary>
    MaxTokens,

    /// <summary>The provider declined or filtered the response (safety/content policy).</summary>
    ContentFilter,

    /// <summary>The caller cancelled the request (client disconnect / request abort).</summary>
    Cancelled,

    /// <summary>The call failed (network, auth, malformed provider response, etc.).</summary>
    Error
}

/// <summary>
/// One event in a streamed provider-neutral chat turn. A single flat shape (rather than a
/// discriminated hierarchy) so the SSE writer and the AOT JSON source-gen context only need one
/// <c>JsonTypeInfo</c>; unused fields for a given <see cref="Type"/> are simply null and omitted
/// from the wire payload.
/// </summary>
public sealed class StudioAiChatEvent
{
    /// <summary>Discriminates which fields below are populated.</summary>
    public required StudioAiChatEventType Type { get; init; }

    /// <summary>Model id actually used, set on <see cref="StudioAiChatEventType.MessageStart"/>.</summary>
    public string? Model { get; init; }

    /// <summary>Incremental assistant text, set on <see cref="StudioAiChatEventType.TextDelta"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Tool call id, set on <see cref="StudioAiChatEventType.ToolCallStart"/> / <c>ToolCallDelta</c> / <c>ToolCallStop</c>.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool name, set on <see cref="StudioAiChatEventType.ToolCallStart"/>.</summary>
    public string? ToolName { get; init; }

    /// <summary>Incremental JSON-argument text, set on <see cref="StudioAiChatEventType.ToolCallDelta"/>.</summary>
    public string? ToolArgumentsDelta { get; init; }

    /// <summary>Full parsed arguments, set on <see cref="StudioAiChatEventType.ToolCallStop"/> when assembly succeeded.</summary>
    public JsonElement? ToolArguments { get; init; }

    /// <summary>Stop reason, set on <see cref="StudioAiChatEventType.MessageStop"/>.</summary>
    public StudioAiStopReason? StopReason { get; init; }

    /// <summary>Prompt token count, set on <see cref="StudioAiChatEventType.MessageStop"/> when the provider reports it.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Completion token count, set on <see cref="StudioAiChatEventType.MessageStop"/> when the provider reports it.</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>Wall-clock time from request dispatch to this event, set on <see cref="StudioAiChatEventType.MessageStop"/> / <c>Error</c>.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>Human-readable failure detail, set on <see cref="StudioAiChatEventType.Error"/>.</summary>
    public string? ErrorMessage { get; init; }
}
