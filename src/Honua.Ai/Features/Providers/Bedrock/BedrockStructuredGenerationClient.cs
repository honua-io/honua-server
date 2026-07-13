// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.AI;

namespace Honua.Ai.Providers.Bedrock;

/// <summary>
/// Outcome of a single structured Bedrock generation turn. Either <see cref="Json"/> carries the
/// model's structured proposal (the forced tool-call input, serialized), or <see cref="Error"/>
/// describes why no proposal was produced.
/// </summary>
internal readonly record struct BedrockStructuredResult(string? Json, string? Error)
{
    internal bool Succeeded => Json is not null;

    internal static BedrockStructuredResult Ok(string json) => new(json, null);

    internal static BedrockStructuredResult Failed(string error) => new(null, error);
}

/// <summary>
/// Shared Bedrock backend for the studio generation flows that otherwise speak to an
/// OpenAI-compatible <c>/chat/completions</c> endpoint with a strict <c>json_schema</c> response
/// format (dashboard, report, and the other Vega-Lite document generators).
///
/// Bedrock's Converse API has no <c>json_schema</c> response format, so structured output is
/// obtained the same way the Anthropic provider does it: a single tool (<c>emit_document</c>) is
/// declared with the proposal schema as its <c>input_schema</c> and the tool choice is forced, so
/// the model must return the proposal as the tool-call input. That input is serialized back to the
/// JSON string the calling service already knows how to deserialize — making Bedrock a drop-in
/// alternative to the OpenAI-compatible path keyed purely on provider id.
/// </summary>
internal static class BedrockStructuredGenerationClient
{
    private const string ToolName = "emit_document";

    /// <summary>
    /// Runs one structured generation turn against Bedrock and returns the proposal JSON.
    /// </summary>
    /// <param name="options">Resolved provider options (model, region, max tokens, timeout).</param>
    /// <param name="model">Effective model id (request override or provider default).</param>
    /// <param name="systemPrompt">System grounding prompt.</param>
    /// <param name="userPrompt">User prompt.</param>
    /// <param name="schema">The proposal JSON schema the tool input must conform to.</param>
    /// <param name="toolDescription">Human-readable description of what the tool emits.</param>
    /// <param name="chatClientFactory">
    /// Factory used to build the <see cref="IChatClient"/>. Injected so tests can substitute a fake
    /// client; in production this builds a real <see cref="BedrockChatClientAdapter"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<BedrockStructuredResult> GenerateAsync(
        WorkflowGenerationProviderOptions options,
        string model,
        string systemPrompt,
        string userPrompt,
        JsonElement schema,
        string toolDescription,
        Func<string, string, string?, IChatClient> chatClientFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(chatClientFactory);

        using var client = chatClientFactory(model, options.Region, NullIfEmpty(options.ApiKey));

        var tool = new EmitDocumentFunction(schema, toolDescription);
        var chatOptions = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = options.MaxTokens,
            Temperature = 0.0f,
            Tools = [tool],
            ToolMode = ChatToolMode.RequireSpecific(ToolName)
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        ChatResponse response;
        try
        {
            response = await client.GetResponseAsync(messages, chatOptions, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BedrockStructuredResult.Failed("Bedrock request timed out.");
        }

        if (response.FinishReason == ChatFinishReason.ContentFilter)
        {
            return BedrockStructuredResult.Failed("Bedrock declined the request (content filtered).");
        }

        // Surface max_tokens truncation explicitly. The Converse adapter maps StopReason.Max_tokens
        // to ChatFinishReason.Length; the forced tool-call JSON is then cut off mid-payload and the
        // caller's deserialize fails with an opaque generic parse error instead of an actionable
        // message (mirrors the workflow/OpenAI/Anthropic providers from #1979).
        if (response.FinishReason == ChatFinishReason.Length)
        {
            return BedrockStructuredResult.Failed(
                "Bedrock response was truncated (max_tokens reached); try a higher MaxTokens.");
        }

        var call = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(c => string.Equals(c.Name, ToolName, StringComparison.Ordinal));

        if (call is null)
        {
            return BedrockStructuredResult.Failed("Bedrock did not return the expected tool output.");
        }

        // The Converse adapter surfaces tool-call arguments as a string->object map; the map IS
        // the proposal object, so write it straight back to JSON for the caller to parse. The
        // writer is hand-rolled (rather than reflection-based serialization) to stay AOT-safe.
        var json = ArgumentsToJson(call.Arguments);
        return BedrockStructuredResult.Ok(json);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Serializes the tool-call argument map (the <c>object?</c> graph the Converse adapter builds
    /// from Bedrock <c>Document</c> values) to JSON without reflection-based serialization, so the
    /// result is AOT-safe and round-trips cleanly to the caller's strongly-typed proposal.
    /// </summary>
    private static string ArgumentsToJson(IDictionary<string, object?>? arguments)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteObject(writer, arguments ?? new Dictionary<string, object?>());
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteObject(Utf8JsonWriter writer, IDictionary<string, object?> map)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in map)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case IDictionary<string, object?> nested:
                WriteObject(writer, nested);
                break;
            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// An <see cref="AIFunction"/> whose JSON schema is the caller's proposal schema. The body is
    /// never invoked — the function exists only to carry the schema and capture the model's
    /// structured output as the forced tool-call input.
    /// </summary>
    private sealed class EmitDocumentFunction : AIFunction
    {
        private readonly JsonElement _schema;

        internal EmitDocumentFunction(JsonElement schema, string description)
        {
            _schema = schema;
            Description = description;
        }

        public override string Name => ToolName;

        public override string Description { get; }

        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }
}
