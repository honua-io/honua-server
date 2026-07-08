// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// An MCP <c>elicitation/create</c> request payload (the <c>params</c> object).
/// Carries a human-readable <see cref="Message"/> and a
/// <see cref="RequestedSchema"/> the client renders into an input form.
/// </summary>
internal sealed class McpElicitationRequest
{
    /// <summary>Human-readable prompt describing what is being requested and why.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The restricted JSON Schema (flat object of primitive properties) describing
    /// the expected response, per the MCP elicitation schema subset.
    /// </summary>
    [JsonPropertyName("requestedSchema")]
    public McpElicitationSchema RequestedSchema { get; set; } = new();
}

/// <summary>
/// The <c>requestedSchema</c> of an elicitation request: a flat <c>object</c>
/// whose properties are primitive schemas keyed by clarification questionId.
/// </summary>
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

/// <summary>
/// A single primitive property of an elicitation <c>requestedSchema</c>. Only the
/// MCP-permitted primitive shapes are expressed: <c>string</c> (optionally an
/// enum via <see cref="Enum"/>/<see cref="EnumNames"/>) and <c>boolean</c>.
/// Arrays and nested objects are intentionally never emitted — the MCP
/// elicitation subset forbids them.
/// </summary>
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

/// <summary>
/// Maps Honua's canonical clarification envelope onto an MCP-native elicitation
/// request and gates the projection on the calling session having advertised the
/// elicitation capability (honua-server#2484).
/// </summary>
internal static class ClarificationElicitationMapper
{
    private const string StringType = "string";
    private const string BooleanType = "boolean";

    /// <summary>
    /// Projects a clarification envelope onto <paramref name="output"/> as an MCP
    /// elicitation request when <paramref name="clientSupportsElicitation"/> is
    /// <c>true</c> and every question is representable in the elicitation schema
    /// subset. On a successful projection the proprietary
    /// <see cref="McpGroundingOutput.Clarification"/> envelope is cleared so the
    /// client drives the native elicitation path; otherwise the envelope is left
    /// intact (graceful fallback) and no elicitation is emitted.
    /// </summary>
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

    /// <summary>
    /// Maps a clarification envelope to an MCP elicitation request, or returns
    /// <c>null</c> when any question cannot be expressed in the elicitation schema
    /// subset (multi-select, blank/duplicate ids). Exposed for direct unit tests
    /// of the mapping in isolation from the tool/session plumbing.
    /// </summary>
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

    /// <summary>
    /// Returns <c>true</c> when the session identified by the request's
    /// <c>Mcp-Session-Id</c> header advertised the MCP elicitation capability at
    /// <c>initialize</c>. Resolves the process-wide
    /// <see cref="McpSessionManager"/> from the request services; a request with
    /// no session (stateless client) or a host without the manager registered
    /// (isolated unit tests) reports <c>false</c>, i.e. graceful fallback.
    /// </summary>
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
