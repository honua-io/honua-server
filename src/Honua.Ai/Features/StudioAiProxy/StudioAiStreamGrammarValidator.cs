// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.StudioAiProxy.Domain;

namespace Honua.Ai.StudioAiProxy;

/// <summary>Validates the provider-neutral event grammar before terminal success is released.</summary>
internal sealed class StudioAiStreamGrammarValidator
{
    internal const string InvalidStreamCode = "studio_ai/invalid_provider_stream";

    private readonly HashSet<string> _seenToolIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openToolIds = new(StringComparer.Ordinal);
    private bool _started;
    private bool _terminal;

    public bool SawSuccessfulTerminal { get; private set; }

    public string? Validate(StudioAiChatEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_terminal)
        {
            return "event_after_terminal";
        }

        switch (evt.Type)
        {
            case StudioAiChatEventType.MessageStart:
                if (_started)
                {
                    return "duplicate_message_start";
                }

                if (string.IsNullOrWhiteSpace(evt.Model))
                {
                    return "missing_provider_model";
                }

                _started = true;
                return null;

            case StudioAiChatEventType.TextDelta:
                return RequireStarted(evt.Text is not null ? null : "missing_text_delta");

            case StudioAiChatEventType.ToolCallStart:
                if (RequireStarted() is { } startError)
                {
                    return startError;
                }

                if (string.IsNullOrWhiteSpace(evt.ToolCallId) || string.IsNullOrWhiteSpace(evt.ToolName))
                {
                    return "invalid_tool_start";
                }

                if (!_seenToolIds.Add(evt.ToolCallId) || !_openToolIds.Add(evt.ToolCallId))
                {
                    return "duplicate_tool_id";
                }

                return null;

            case StudioAiChatEventType.ToolCallDelta:
                if (string.IsNullOrWhiteSpace(evt.ToolCallId) || !_openToolIds.Contains(evt.ToolCallId))
                {
                    return "tool_delta_without_start";
                }

                return evt.ToolArgumentsDelta is null ? "missing_tool_delta" : null;

            case StudioAiChatEventType.ToolCallStop:
                if (string.IsNullOrWhiteSpace(evt.ToolCallId) || !_openToolIds.Remove(evt.ToolCallId))
                {
                    return "tool_stop_without_start";
                }

                return evt.ToolArguments is null ? "invalid_tool_arguments" : null;

            case StudioAiChatEventType.MessageStop:
                if (RequireStarted() is { } messageStartError)
                {
                    return messageStartError;
                }

                if (_openToolIds.Count != 0)
                {
                    return "message_stop_before_tool_stop";
                }

                if (evt.StopReason is null or StudioAiStopReason.Error or StudioAiStopReason.Cancelled)
                {
                    return "invalid_stop_reason";
                }

                _terminal = true;
                SawSuccessfulTerminal = true;
                return null;

            case StudioAiChatEventType.Error:
                _terminal = true;
                return null;

            case StudioAiChatEventType.TranscriptProvenance:
                return "adapter_emitted_provenance";

            default:
                return "unknown_event_type";
        }
    }

    private string? RequireStarted(string? error = null)
        => !_started ? "missing_message_start" : error;
}
