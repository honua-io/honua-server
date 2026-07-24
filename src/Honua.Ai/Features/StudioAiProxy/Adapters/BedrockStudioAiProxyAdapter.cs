// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.StudioAiProxy.Adapters;

/// <summary>
/// Adapter that bridges the Studio AI proxy onto the existing Bedrock chat-client plumbing
/// (<see cref="IBedrockChatClientFactory"/> / <c>BedrockChatClientAdapter</c>, which already speaks
/// Bedrock's Converse streaming API as a Microsoft.Extensions.AI <see cref="IChatClient"/>). This is
/// the "thin bridge" called for by honua-server#3000: no new AWS wire code, just a translation from
/// the proxy's provider-neutral request/event shape to/from <see cref="ChatMessage"/> /
/// <see cref="ChatResponseUpdate"/>.
/// </summary>
/// <remarks>
/// One behavioral difference from the Anthropic/OpenAI-compatible adapters: Bedrock's Converse
/// streaming API accumulates a tool call's JSON arguments internally and hands the proxy a single
/// complete <see cref="FunctionCallContent"/> at the end of the tool-use content block (see
/// <c>BedrockChatClientAdapter.GetStreamingResponseAsync</c>), rather than incremental JSON-text
/// fragments. This adapter still emits the full <c>ToolCallStart</c> / <c>ToolCallDelta</c> /
/// <c>ToolCallStop</c> triad for contract symmetry with the other two adapters, but the delta is a
/// single chunk carrying the whole arguments payload instead of a token-by-token stream.
/// </remarks>
internal sealed class BedrockStudioAiProxyAdapter : IStudioAiProxyAdapter
{
    private const string ProviderLabel = "bedrock";

    private readonly IBedrockChatClientFactory _chatClientFactory;
    private readonly StudioAiProxyApiKeyResolver _apiKeyResolver;
    private readonly ILogger<BedrockStudioAiProxyAdapter> _logger;

    public BedrockStudioAiProxyAdapter(
        IBedrockChatClientFactory chatClientFactory,
        StudioAiProxyApiKeyResolver apiKeyResolver,
        ILogger<BedrockStudioAiProxyAdapter> logger)
    {
        _chatClientFactory = chatClientFactory;
        _apiKeyResolver = apiKeyResolver;
        _logger = logger;
    }

    public string Kind => StudioAiProxyConfiguration.BedrockKind;

    public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options)
        => !string.IsNullOrWhiteSpace(options.Model);

    public async IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model!;
        var apiKey = await _apiKeyResolver.ResolveAsync(ProviderLabel, options, cancellationToken).ConfigureAwait(false);

        using var client = _chatClientFactory.Create(model, options.Region, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);

        var messages = BuildMessages(request);
        var chatOptions = BuildChatOptions(options, request, model);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();

        yield return new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = model };

        var enumerator = client.GetStreamingResponseAsync(messages, chatOptions, timeoutCts.Token).GetAsyncEnumerator(timeoutCts.Token);
        int? promptTokens = null;
        int? completionTokens = null;
        var stopReason = StudioAiStopReason.EndTurn;
        var sawFinish = false;

        try
        {
            while (true)
            {
                var (hasNext, timedOut, failure) = await MoveNextSafeAsync(enumerator, cancellationToken).ConfigureAwait(false);

                if (timedOut)
                {
                    StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
                    yield return Error(model, "Provider request timed out.", stopwatch.ElapsedMilliseconds);
                    yield break;
                }

                if (failure is not null)
                {
                    StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, failure);
                    yield return Error(model, "Provider request failed.", stopwatch.ElapsedMilliseconds);
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                var update = enumerator.Current;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            yield return new StudioAiChatEvent { Type = StudioAiChatEventType.TextDelta, Text = text.Text };
                            break;

                        case FunctionCallContent call:
                            yield return new StudioAiChatEvent
                            {
                                Type = StudioAiChatEventType.ToolCallStart,
                                ToolCallId = call.CallId,
                                ToolName = call.Name
                            };

                            var argumentsJson = SerializeArguments(call.Arguments);
                            yield return new StudioAiChatEvent
                            {
                                Type = StudioAiChatEventType.ToolCallDelta,
                                ToolCallId = call.CallId,
                                ToolArgumentsDelta = argumentsJson
                            };
                            yield return new StudioAiChatEvent
                            {
                                Type = StudioAiChatEventType.ToolCallStop,
                                ToolCallId = call.CallId,
                                ToolArguments = TryParse(argumentsJson)
                            };
                            break;

                        case UsageContent usage:
                            promptTokens = (int?)usage.Details.InputTokenCount ?? promptTokens;
                            completionTokens = (int?)usage.Details.OutputTokenCount ?? completionTokens;
                            break;
                    }
                }

                if (update.FinishReason is { } finishReason)
                {
                    sawFinish = true;
                    stopReason = MapFinishReason(finishReason);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!sawFinish)
        {
            yield return Error(model, "Provider stream ended before a finish reason was received.", stopwatch.ElapsedMilliseconds);
            yield break;
        }

        yield return new StudioAiChatEvent
        {
            Type = StudioAiChatEventType.MessageStop,
            StopReason = stopReason,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            LatencyMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static async Task<(bool HasNext, bool TimedOut, Exception? Failure)> MoveNextSafeAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator,
        CancellationToken callerToken)
    {
        try
        {
            var hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            return (hasNext, false, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (false, true, null);
        }
        // Intentional catch-all: this is the provider-boundary call to the Bedrock SDK, which
        // surfaces transport/auth/throttling failures beyond a specific type; map any remaining
        // failure to an Error event instead of crashing the stream (mirrors BedrockWorkflowGenerationProvider).
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, false, ex);
        }
    }

    private static List<ChatMessage> BuildMessages(StudioAiChatRequest request)
    {
        List<ChatMessage> messages = [];
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.System));
        }

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case StudioAiRole.System:
                    messages.Add(new ChatMessage(ChatRole.System, message.Content));
                    break;
                case StudioAiRole.Assistant:
                    if (message.ToolCalls is { Count: > 0 } toolCalls)
                    {
                        List<AIContent> contents = [];
                        if (!string.IsNullOrEmpty(message.Content))
                        {
                            contents.Add(new TextContent(message.Content));
                        }

                        contents.AddRange(toolCalls.Select(static call =>
                            (AIContent)new FunctionCallContent(
                                call.Id,
                                call.Name,
                                ToArgumentsDictionary(call.Arguments))));
                        messages.Add(new ChatMessage(ChatRole.Assistant, contents));
                    }
                    else
                    {
                        messages.Add(new ChatMessage(ChatRole.Assistant, message.Content));
                    }
                    break;
                case StudioAiRole.Tool:
                    messages.Add(new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(message.ToolCallId ?? string.Empty, message.Content)]));
                    break;
                default:
                    messages.Add(new ChatMessage(ChatRole.User, message.Content));
                    break;
            }
        }

        return messages;
    }

    private static Dictionary<string, object?> ToArgumentsDictionary(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ToArgumentValue(property.Value),
                StringComparer.Ordinal)
            : [];

    private static object? ToArgumentValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => ToArgumentsDictionary(value),
        JsonValueKind.Array => value.EnumerateArray().Select(ToArgumentValue).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.GetRawText()
    };

    private static ChatOptions BuildChatOptions(StudioAiProxyProviderOptions options, StudioAiChatRequest request, string model)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = request.MaxTokens ?? options.MaxTokens,
            Temperature = request.Temperature is { } temperature ? (float)temperature : null
        };

        var mode = request.ToolChoice?.Mode ?? StudioAiToolChoiceMode.Auto;
        if (request.Tools is { Count: > 0 } tools && mode != StudioAiToolChoiceMode.None)
        {
            chatOptions.Tools = tools.Select(t => (AITool)new StudioAiToolFunction(t)).ToList();
            chatOptions.ToolMode = mode switch
            {
                StudioAiToolChoiceMode.Required => ChatToolMode.RequireAny,
                StudioAiToolChoiceMode.Specific => ChatToolMode.RequireSpecific(request.ToolChoice!.ToolName!),
                _ => ChatToolMode.Auto
            };
        }

        return chatOptions;
    }

    private static StudioAiStopReason MapFinishReason(ChatFinishReason finishReason)
    {
        if (finishReason == ChatFinishReason.ToolCalls)
        {
            return StudioAiStopReason.ToolCall;
        }

        if (finishReason == ChatFinishReason.Length)
        {
            return StudioAiStopReason.MaxTokens;
        }

        if (finishReason == ChatFinishReason.ContentFilter)
        {
            return StudioAiStopReason.ContentFilter;
        }

        return StudioAiStopReason.EndTurn;
    }

    /// <summary>Writes a tool call's arguments dictionary to a compact JSON object without reflection (AOT-safe).</summary>
    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
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
                writer.WriteStartObject();
                foreach (var (key, nestedValue) in nested)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, nestedValue);
                }

                writer.WriteEndObject();
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

    private static JsonElement? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StudioAiChatEvent Error(string model, string message, long? latencyMs = null) => new()
    {
        Type = StudioAiChatEventType.Error,
        Model = model,
        ErrorMessage = message,
        LatencyMs = latencyMs
    };

    /// <summary>
    /// An <see cref="AIFunction"/> whose schema is one <see cref="StudioAiToolDefinition"/>; never
    /// invoked (Bedrock/Converse only needs the declaration to steer the model), it exists solely to
    /// carry name/description/schema through <see cref="ChatOptions.Tools"/>. Mirrors the
    /// <c>EmitWorkflowFunction</c> pattern already used by <c>BedrockWorkflowGenerationProvider</c>.
    /// </summary>
    private sealed class StudioAiToolFunction : AIFunction
    {
        private readonly StudioAiToolDefinition _definition;

        internal StudioAiToolFunction(StudioAiToolDefinition definition) => _definition = definition;

        public override string Name => _definition.Name;

        public override string Description => _definition.Description ?? _definition.Name;

        public override JsonElement JsonSchema => _definition.InputSchema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>(null);
    }
}
