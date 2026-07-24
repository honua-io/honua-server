// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>
/// Maps the wire-shaped <see cref="StudioAiChatHttpRequest"/> (lower-case string enums, as JSON
/// clients naturally write them) onto the internal <see cref="StudioAiChatRequest"/> contract,
/// rejecting unknown enum spellings with a caller-facing message rather than letting them silently
/// default.
/// </summary>
public static class StudioAiChatRequestMapper
{
    /// <summary>Converts <paramref name="http"/> to a domain request, or returns a rejection reason.</summary>
    public static (StudioAiChatRequest? Request, string? Error) ToDomain(StudioAiChatHttpRequest http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.Messages is not { Count: > 0 } sourceMessages)
        {
            return (null, "At least one message is required.");
        }

        var messages = new List<StudioAiMessage>(sourceMessages.Count);
        foreach (var message in sourceMessages)
        {
            if (message is null || message.Content is null)
            {
                return (null, "Message content must not be null.");
            }

            if (!TryParseRole(message.Role, out var role))
            {
                return (null, $"Unknown message role '{message.Role}'. Expected one of: system, user, assistant, tool.");
            }

            IReadOnlyList<StudioAiToolCall>? toolCalls = null;
            if (message.ToolCalls is { Count: > 0 } sourceToolCalls)
            {
                if (role != StudioAiRole.Assistant)
                {
                    return (null, "message.toolCalls is only valid for assistant messages.");
                }

                if (sourceToolCalls.Any(static call =>
                        string.IsNullOrWhiteSpace(call.Id) ||
                        string.IsNullOrWhiteSpace(call.Name) ||
                        call.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object))
                {
                    return (null, "Each assistant tool call requires a non-empty id, name, and JSON object arguments value.");
                }

                toolCalls = sourceToolCalls
                    .Select(static call => new StudioAiToolCall
                    {
                        Id = call.Id,
                        Name = call.Name,
                        Arguments = call.Arguments.Clone()
                    })
                    .ToArray();
            }

            messages.Add(new StudioAiMessage
            {
                Role = role,
                Content = message.Content,
                ToolCallId = message.ToolCallId,
                ToolName = message.ToolName,
                ToolCalls = toolCalls
            });
        }

        List<StudioAiToolDefinition>? tools = null;
        if (http.Tools is { Count: > 0 })
        {
            tools = http.Tools
                .Select(t => new StudioAiToolDefinition { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema })
                .ToList();
        }

        StudioAiToolChoice? toolChoice = null;
        if (http.ToolChoice is not null)
        {
            if (!TryParseToolChoiceMode(http.ToolChoice.Mode, out var mode))
            {
                return (null, $"Unknown toolChoice.mode '{http.ToolChoice.Mode}'. Expected one of: auto, none, required, specific.");
            }

            toolChoice = new StudioAiToolChoice { Mode = mode, ToolName = http.ToolChoice.ToolName };
        }

        var request = new StudioAiChatRequest
        {
            Provider = http.Provider,
            Model = http.Model,
            System = http.System,
            Messages = messages,
            Tools = tools,
            ToolChoice = toolChoice,
            MaxTokens = http.MaxTokens,
            Temperature = http.Temperature
        };

        return (request, null);
    }

    private static bool TryParseRole(string role, out StudioAiRole parsed)
    {
        switch ((role ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "system":
                parsed = StudioAiRole.System;
                return true;
            case "user":
                parsed = StudioAiRole.User;
                return true;
            case "assistant":
                parsed = StudioAiRole.Assistant;
                return true;
            case "tool":
                parsed = StudioAiRole.Tool;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static bool TryParseToolChoiceMode(string mode, out StudioAiToolChoiceMode parsed)
    {
        switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "auto":
                parsed = StudioAiToolChoiceMode.Auto;
                return true;
            case "none":
                parsed = StudioAiToolChoiceMode.None;
                return true;
            case "required":
                parsed = StudioAiToolChoiceMode.Required;
                return true;
            case "specific":
                parsed = StudioAiToolChoiceMode.Specific;
                return true;
            default:
                parsed = default;
                return false;
        }
    }
}
