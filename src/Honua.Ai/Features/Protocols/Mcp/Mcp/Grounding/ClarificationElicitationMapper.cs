// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Grounding;

// -----------------------------------------------------------------------
// MCP-native elicitation payload (honua-server#2484)
//
// MCP 2025-06-18 elicitation lets a server request structured input from the
// user through the client (`elicitation/create`: message + requestedSchema).
// Honua's deterministic, stateless MCP tools do not own the interaction loop
// (ADR-0028: the model/agent runs client-side), so rather than performing a
// server-initiated round-trip the grounding tools hand the elicitation payload
// back to the client inside the tool result. An elicitation-capable client
// renders `requestedSchema` as a native form, collects the answers, and replays
// them through honua_clarify_intent (answers keyed by the same questionId). When
// the client did not advertise the elicitation capability the tools fall back to
// the proprietary clarification envelope unchanged.
// -----------------------------------------------------------------------

internal sealed class McpElicitationRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("requestedSchema")]
    public McpElicitationSchema RequestedSchema { get; set; } = new();
}

internal sealed class McpElicitationSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, McpElicitationProperty> Properties { get; set; }
        = new Dictionary<string, McpElicitationProperty>(StringComparer.Ordinal);

    [JsonPropertyName("required")]
    public IReadOnlyList<string> Required { get; set; } = [];
}

// Arrays and nested objects are never emitted because the MCP elicitation schema subset forbids them.
internal sealed class McpElicitationProperty
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enum")]
    public IReadOnlyList<string>? Enum { get; set; }

    [JsonPropertyName("enumNames")]
    public IReadOnlyList<string>? EnumNames { get; set; }
}

internal static class ClarificationElicitationMapper
{
    private const string StringType = "string";
    private const string BooleanType = "boolean";

    public static void ProjectOntoOutput(
        McpGroundingOutput output,
        ClarificationRequest? clarification,
        bool clientSupportsElicitation)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!clientSupportsElicitation || clarification is null)
        {
            return;
        }

        var elicitation = TryMap(clarification);
        if (elicitation is null)
        {
            // Not representable in the elicitation subset (e.g. a multi-select
            // question, which would require an array the subset forbids). Keep the
            // proprietary envelope so the turn still round-trips.
            return;
        }

        output.Elicitation = elicitation;
        output.Clarification = null;
    }

    public static McpElicitationRequest? TryMap(ClarificationRequest clarification)
    {
        ArgumentNullException.ThrowIfNull(clarification);

        if (clarification.Questions.Count == 0)
        {
            return null;
        }

        var properties = new Dictionary<string, McpElicitationProperty>(StringComparer.Ordinal);
        var required = new List<string>(clarification.Questions.Count);

        foreach (var question in clarification.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.QuestionId)
                || properties.ContainsKey(question.QuestionId))
            {
                // Blank or duplicate ids would collide as object keys; fall back
                // to the proprietary envelope rather than emit an ambiguous form.
                return null;
            }

            if (!TryMapQuestion(question, out var property))
            {
                return null;
            }

            properties[question.QuestionId] = property;
            required.Add(question.QuestionId);
        }

        return new McpElicitationRequest
        {
            Message = BuildMessage(clarification),
            RequestedSchema = new McpElicitationSchema
            {
                Properties = properties,
                Required = required
            }
        };
    }

    private static bool TryMapQuestion(ClarificationQuestion question, out McpElicitationProperty property)
    {
        switch (question.Kind)
        {
            case ClarificationQuestionKind.SingleSelect when question.Options is { Count: > 0 } options:
                property = new McpElicitationProperty
                {
                    Type = StringType,
                    Description = question.Prompt,
                    Enum = options.Select(o => o.Id).ToArray(),
                    EnumNames = options.Select(o => o.Label).ToArray()
                };
                return true;

            case ClarificationQuestionKind.SingleSelect:
            case ClarificationQuestionKind.FreeText:
                property = new McpElicitationProperty
                {
                    Type = StringType,
                    Description = question.Prompt
                };
                return true;

            case ClarificationQuestionKind.Confirmation:
                property = new McpElicitationProperty
                {
                    Type = BooleanType,
                    Description = question.Prompt
                };
                return true;

            case ClarificationQuestionKind.MultiSelect:
            default:
                // MultiSelect needs an array; the elicitation subset is flat
                // primitives only, so it cannot be represented natively.
                property = null!;
                return false;
        }
    }

    private static string BuildMessage(ClarificationRequest clarification)
    {
        var lead = string.IsNullOrWhiteSpace(clarification.IntentId)
            ? "Additional detail is needed before Honua can continue."
            : $"Additional detail is needed before Honua can continue with intent '{clarification.IntentId}'.";

        if (clarification.ReasonCodes.Count == 0)
        {
            return lead;
        }

        var reasons = string.Join(", ", clarification.ReasonCodes.Select(r => r.ToString()));
        return $"{lead} (reasons: {reasons})";
    }

    public static bool ClientSupportsElicitation(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var sessionId = httpContext.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        var sessions = httpContext.RequestServices?.GetService<McpSessionManager>();
        return sessions is not null && sessions.SupportsElicitation(sessionId);
    }
}
