// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.StudioAiProxy.Domain;

public enum StudioAiChatEventType
{
    MessageStart,

    TextDelta,

    ToolCallStart,

    ToolCallDelta,

    ToolCallStop,

    MessageStop,

    Error
    ,

    TranscriptProvenance
}

public enum StudioAiStopReason
{
    EndTurn,

    ToolCall,

    MaxTokens,

    ContentFilter,

    Cancelled,

    Error
}

// The event is one flat shape so the SSE writer and the AOT JSON source generator share a single JsonTypeInfo.
public sealed class StudioAiChatEvent
{
    public required StudioAiChatEventType Type { get; init; }

    public string? Model { get; init; }

    public string? Text { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? ToolArgumentsDelta { get; init; }

    public JsonElement? ToolArguments { get; init; }

    public StudioAiStopReason? StopReason { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public long? LatencyMs { get; init; }

    public string? ErrorMessage { get; init; }

    // The failure code never contains prompt or key material.
    public string? ErrorCode { get; init; }

    public StudioAiSignedTranscript? Provenance { get; init; }
}
