// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Mrtr;

namespace Honua.Ai.Protocols.Mcp.Grounding;

// Clarification over MRTR. A modern request that declared form elicitation gets the question set as
// an input_required result; the retry resumes the same intent with every answer given so far. The
// signed requestState carries the intent id and those answers, so nothing is held between calls.
internal sealed class ClarificationContinuation
{
    internal const string InputKey = "clarify";

    private ClarificationContinuation(McpRequestContinuation? continuation, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        Continuation = continuation;
        Answers = answers;
    }

    // The request to run instead of the call as written; null when the call runs unchanged.
    public GroundingRequest? Request { get; private init; }

    public bool Declined { get; private init; }

    private McpRequestContinuation? Continuation { get; }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> Answers { get; }

    public static ClarificationContinuation Begin(HttpContext httpContext, McpClarifyIntentArgument original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var continuation = McpRequestContinuation.From(httpContext);
        var given = original.Response?.Answers ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (continuation?.State is not { Kind: McpRequestStateKind.Clarification } state)
        {
            return new ClarificationContinuation(continuation, given);
        }

        var response = continuation.Response(InputKey)
            ?? throw new GeoprocessingValidationException($"A clarification retry requires inputResponses.{InputKey}.");
        var (intentId, earlier) = ReadReference(state.Reference);
        var currentAnswers = response.Accepted
            ? ReadAnswers(response.Content)
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var answers = Merge(given, earlier, currentAnswers);
        var declined = !response.Accepted || currentAnswers.Count == 0;
        var resumed = new McpClarifyIntentArgument
        {
            IntentId = intentId,
            Goal = original.Goal,
            WorkflowFamilyHint = original.WorkflowFamilyHint,
            Constraints = original.Constraints,
            ExplicitInputs = original.ExplicitInputs,
            AssumptionPolicy = original.AssumptionPolicy,
            Context = original.Context,
            Response = new McpClarificationResponseInput { Answers = answers },
        };
        return new ClarificationContinuation(continuation, answers)
        {
            Request = answers.Count > 0 ? GroundingToolMapper.ToDomain(resumed)
                : GroundingToolMapper.ToDomain(new McpGroundCandidatesArgument
                {
                    IntentId = intentId,
                    Goal = original.Goal,
                    WorkflowFamilyHint = original.WorkflowFamilyHint,
                    Constraints = original.Constraints,
                    ExplicitInputs = original.ExplicitInputs,
                    AssumptionPolicy = original.AssumptionPolicy,
                    Context = original.Context,
                }),
            Declined = declined,
        };
    }

    public static McpClarifyIntentArgument FromGround(McpGroundCandidatesArgument? argument) => new()
    {
        IntentId = argument?.IntentId,
        Goal = argument?.Goal,
        WorkflowFamilyHint = argument?.WorkflowFamilyHint,
        Constraints = argument?.Constraints,
        ExplicitInputs = argument?.ExplicitInputs,
        AssumptionPolicy = argument?.AssumptionPolicy,
        Context = argument?.Context,
    };

    // Returns input_required when the request declared form elicitation, the person has not declined,
    // every residual question fits the elicitation subset and the answers fit the state; otherwise null
    // and the caller keeps today's output.
    public McpToolsCallResult? NextInput(ClarificationRequest? clarification)
    {
        if (Declined
            || Continuation is not { Modes.Form: true } continuation
            || clarification is not { IntentId: { Length: > 0 } intentId }
            || ClarificationElicitationMapper.TryMap(clarification) is not { } elicitation)
        {
            return null;
        }

        var reference = WriteReference(intentId, Answers);
        if (!McpRequestStateCodec.FitsReference(reference))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", "form");
            writer.WriteString("message", elicitation.Message);
            writer.WritePropertyName("requestedSchema");
            JsonSerializer.Serialize(writer, elicitation.RequestedSchema, GroundingJsonContext.Default.McpElicitationSchema);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        var grant = new McpRequestStateGrant(
            McpRequestStateKind.Clarification, reference, continuation.Codec.UtcNow + McpRequestStateCodec.ClarificationLifetime);
        return continuation.InputRequired(InputKey, document.RootElement.Clone(), grant);
    }

    private static Dictionary<string, IReadOnlyList<string>> Merge(
        params IReadOnlyDictionary<string, IReadOnlyList<string>>[] rounds)
    {
        var merged = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (questionId, values) in rounds.SelectMany(static round => round))
        {
            merged[questionId] = values;
        }

        return merged;
    }

    private static string WriteReference(string intentId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("i", intentId);
            writer.WriteStartObject("a");
            foreach (var (questionId, values) in answers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartArray(questionId);
                foreach (var value in values)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The reference was written by WriteReference and is tag-verified, so its shape is trusted.
    private static (string IntentId, IReadOnlyDictionary<string, IReadOnlyList<string>> Answers) ReadReference(string reference)
    {
        using var document = JsonDocument.Parse(reference);
        var root = document.RootElement;
        var answers = root.GetProperty("a").EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => (IReadOnlyList<string>)property.Value.EnumerateArray().Select(static value => value.GetString()!).ToArray(),
            StringComparer.Ordinal);
        return (root.GetProperty("i").GetString()!, answers);
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadAnswers(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            throw new GeoprocessingValidationException($"An accepted {InputKey} response requires content.");
        }

        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in content.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                continue;
            }

            answers[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => [property.Value.GetString()!],
                JsonValueKind.True => ["true"],
                JsonValueKind.False => Array.Empty<string>(),
                JsonValueKind.Number => [property.Value.GetRawText()],
                _ => throw new GeoprocessingValidationException(
                    $"Clarification answer '{property.Name}' must be a string, boolean or number."),
            };
        }

        return answers;
    }
}
