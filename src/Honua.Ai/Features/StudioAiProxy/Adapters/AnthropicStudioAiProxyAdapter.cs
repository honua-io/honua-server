// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.StudioAiProxy.Adapters;

internal sealed class AnthropicStudioAiProxyAdapter : IStudioAiProxyAdapter, IStudioAiPreparedHttpAdapter
{
    private const string AnthropicVersion = "2023-06-01";
    private const string ProviderLabel = "anthropic";
    private const int MaxMetadataLabelLength = 64;
    internal const string UnsupportedContentBlockCode = "studio_ai/unsupported_content_block";
    internal const string ProviderStreamErrorCode = "studio_ai/provider_error";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StudioAiProxyApiKeyResolver _apiKeyResolver;
    private readonly ILogger<AnthropicStudioAiProxyAdapter> _logger;
    private readonly AnthropicContinuationStore _continuations;

    public AnthropicStudioAiProxyAdapter(
        IHttpClientFactory httpClientFactory,
        StudioAiProxyApiKeyResolver apiKeyResolver,
        ILogger<AnthropicStudioAiProxyAdapter> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _apiKeyResolver = apiKeyResolver;
        _logger = logger;
        _continuations = new AnthropicContinuationStore(timeProvider ?? TimeProvider.System);
    }

    public string Kind => StudioAiProxyConfiguration.AnthropicKind;

    public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            return false;
        }

        // Anthropic always requires a key. A plain configured value or secret reference counts
        // (actual secret-store resolution happens later, in StreamAsync); otherwise the
        // per-provider environment variable must be set. Without either, the provider cannot
        // ever be called, so it must report unconfigured here rather than let a chat request
        // commit a 200 SSE stream that immediately errors with "API key is not configured."
        return !string.IsNullOrWhiteSpace(options.ApiKey)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StudioAiProxyApiKeyResolver.EnvVarName(providerName)));
    }

    public async IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
        StudioAiProxyProviderOptions options, StudioAiChatRequest request,
        IAiExecutionPermit permit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        // Preserve direct callers' lazy enumeration and initial cancellation gate.
        await permit.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var invocation = Prepare(options, request);
        await foreach (var evt in StreamPreparedAsync(invocation, permit, cancellationToken).ConfigureAwait(false))
            yield return evt;
    }

    public StudioAiHttpInvocation Prepare(StudioAiProxyProviderOptions options, StudioAiChatRequest request,
        AiExecutionContext? executionContext = null)
    {
        string? requiredCredentialFingerprint = null;
        var identity = executionContext ?? request.ExecutionContext;
        return new(options, request, "/v1/messages", (snapshot, original, model) =>
        {
            var scope = new AnthropicContinuationStore.Scope(snapshot, original, model, identity);
            var body = JsonSerializer.SerializeToUtf8Bytes(BuildRequest(scope), StudioAiProxyJsonContext.Default.AnthropicProxyRequest);
            requiredCredentialFingerprint = scope.RestoredCredentials.SingleOrDefault();
            return body;
        }, identity)
        { RequiredCredentialFingerprint = requiredCredentialFingerprint };
    }

    public async IAsyncEnumerable<StudioAiChatEvent> StreamPreparedAsync(
        StudioAiHttpInvocation invocation, IAiExecutionPermit permit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await permit.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        var options = invocation.CopyOptions();
        var state = new AnthropicStreamState(invocation.Model, _logger);
        var turn = new AnthropicAssistantTurn();
        string? apiKey = null;
        var credentialUnavailable = false;
        try
        {
            apiKey = await _apiKeyResolver.ResolveAsync(ProviderLabel, options, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioAiProxyCredentialUnavailableException)
        {
            credentialUnavailable = true;
        }

        if (credentialUnavailable)
        {
            yield return state.Error("Provider credentials are unavailable.", errorCode: StudioAiProxyApiKeyResolver.CredentialUnavailableCode);
            yield break;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return state.Error("Anthropic API key is not configured.");
            yield break;
        }

        var credentialFingerprint = AnthropicContinuationStore.CredentialFingerprint(apiKey);
        if (invocation.RequiredCredentialFingerprint is { } required && required != credentialFingerprint)
            throw new InvalidOperationException(AnthropicContinuationStore.UnavailableMessage);

        var client = _httpClientFactory.CreateClient("studio-ai-proxy");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, invocation.Destination);
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        httpRequest.Content = new ByteArrayContent(invocation.CopyBody());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        state.Timer.Start();

        var (response, sendTimedOut, requestError) = await StudioAiProxyHttpStreamHelpers
            .SendSafeAsync(client, httpRequest, permit, timeoutCts.Token, cancellationToken)
            .ConfigureAwait(false);

        if (sendTimedOut)
        {
            StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
            yield return state.Error("Provider request timed out.");
            yield break;
        }

        if (requestError is not null)
        {
            StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, "send", requestError.GetType().Name);
            yield return state.Error("Provider request failed.");
            yield break;
        }

        using var successResponse = response!;
        if (!successResponse.IsSuccessStatusCode)
        {
            var (_, bodyTimedOut, bodyFailure, _) = await StudioAiProxyHttpStreamHelpers
                .ReadErrorBodySafeAsync(successResponse.Content, timeoutCts.Token, cancellationToken)
                .ConfigureAwait(false);
            if (bodyTimedOut)
            {
                StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
                yield return state.Error("Provider request timed out.", state.Timer.ElapsedMilliseconds);
                yield break;
            }

            if (bodyFailure is not null)
            {
                StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, "error-body", bodyFailure.GetType().Name);
                yield return state.Error("Provider response failed.", state.Timer.ElapsedMilliseconds);
                yield break;
            }

            StudioAiProxyLog.ProviderHttpError(_logger, ProviderLabel, (int)successResponse.StatusCode);
            yield return state.Error($"Provider returned HTTP {(int)successResponse.StatusCode}.");
            yield break;
        }

        var (stream, openTimedOut, openFailure) = await StudioAiProxyHttpStreamHelpers
            .OpenStreamSafeAsync(successResponse.Content, timeoutCts.Token, cancellationToken)
            .ConfigureAwait(false);
        if (openTimedOut)
        {
            StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
            yield return state.Error("Provider request timed out.", state.Timer.ElapsedMilliseconds);
            yield break;
        }

        if (openFailure is not null)
        {
            StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, "open-stream", openFailure.GetType().Name);
            yield return state.Error("Provider response failed.", state.Timer.ElapsedMilliseconds);
            yield break;
        }

        await using var responseStream = stream!;
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        while (true)
        {
            var (line, readTimedOut, ioError) = await StudioAiProxyHttpStreamHelpers
                .ReadLineSafeAsync(reader, timeoutCts.Token, cancellationToken)
                .ConfigureAwait(false);

            if (readTimedOut)
            {
                StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
                yield return state.Error("Provider request timed out.", state.Timer.ElapsedMilliseconds);
                yield break;
            }

            if (ioError is not null)
            {
                StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, "read-stream", ioError.GetType().Name);
                yield return state.Error("Provider stream ended unexpectedly.", state.Timer.ElapsedMilliseconds);
                yield break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].TrimStart();
            if (payload.Length == 0)
            {
                continue;
            }

            if (state.SawMessageStop)
            {
                yield return state.Error("Provider returned a stream frame after message_stop.", state.Timer.ElapsedMilliseconds,
                    StudioAiStreamGrammarValidator.InvalidStreamCode);
                yield break;
            }

            var frame = ParseFrame(payload);
            if (frame is null)
            {
                yield return state.Error("Provider returned a malformed stream frame.", state.Timer.ElapsedMilliseconds,
                    StudioAiStreamGrammarValidator.InvalidStreamCode);
                yield break;
            }

            var (trackingError, stoppedContentBlockType) = state.TrackContentBlock(frame);
            if (trackingError is not null)
            {
                yield return state.Error(trackingError, state.Timer.ElapsedMilliseconds,
                    StudioAiStreamGrammarValidator.InvalidStreamCode);
                yield break;
            }
            turn.Capture(frame, payload);

            switch (frame.Type)
            {
                case "message_start":
                    state.SawMessageStart = true;
                    state.PromptTokens = frame.Message?.Usage?.InputTokens ?? state.PromptTokens;
                    state.CompletionTokens = frame.Message?.Usage?.OutputTokens ?? state.CompletionTokens;
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.MessageStart,
                        Model = frame.Message?.Model
                    };
                    break;

                case "content_block_start" when frame.ContentBlock?.Type == "tool_use":
                    state.ToolCallId = frame.ContentBlock.Id;
                    state.ToolArgsBuffer = new StringBuilder();
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.ToolCallStart,
                        ToolCallId = state.ToolCallId,
                        ToolName = frame.ContentBlock.Name
                    };
                    break;

                case "content_block_start" when frame.ContentBlock?.Type == "text":
                case "content_block_stop" when stoppedContentBlockType is "text" or "thinking" or "redacted_thinking":
                case "content_block_start" when frame.ContentBlock?.Type is "thinking" or "redacted_thinking" && state.SawMessageStart:
                case "ping":
                    break;

                case "content_block_delta" when frame.Delta?.Type is "thinking_delta" or "signature_delta"
                    && frame.Index is { } thinkingIndex && state.OpenContentBlocks.GetValueOrDefault(thinkingIndex) == "thinking":
                    break;

                case "content_block_delta" when frame.Index is { } blockIndex
                    && state.OpenContentBlocks.GetValueOrDefault(blockIndex) is "thinking" or "redacted_thinking":
                    yield return state.RejectFrame(frame, payload.Length, stoppedContentBlockType);
                    yield break;

                case "content_block_delta" when frame.Delta?.Type == "text_delta" && frame.Delta.Text is ""
                    && state.SawMessageStart && frame.Index is { } emptyTextIndex && state.OpenContentBlocks.GetValueOrDefault(emptyTextIndex) == "text":
                    break;

                case "content_block_delta" when frame.Delta?.Type == "text_delta" && frame.Delta.Text is { Length: > 0 } text:
                    yield return new StudioAiChatEvent { Type = StudioAiChatEventType.TextDelta, Text = text };
                    break;

                case "content_block_delta" when frame.Delta?.Type == "input_json_delta" && frame.Delta.PartialJson is { } partial:
                    state.ToolArgsBuffer?.Append(partial);
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.ToolCallDelta,
                        ToolCallId = state.ToolCallId,
                        ToolArgumentsDelta = partial
                    };
                    break;

                case "content_block_stop" when stoppedContentBlockType == "tool_use" && state.ToolCallId is not null:
                    var arguments = TryParse(state.ToolArgsBuffer?.ToString());
                    if (arguments is null)
                    {
                        yield return state.Error("Provider returned invalid or incomplete tool arguments.", state.Timer.ElapsedMilliseconds,
                            StudioAiStreamGrammarValidator.InvalidStreamCode);
                        yield break;
                    }

                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.ToolCallStop,
                        ToolCallId = state.ToolCallId,
                        ToolArguments = arguments
                    };
                    state.ToolCallId = null;
                    state.ToolArgsBuffer = null;
                    break;

                case "message_delta":
                    state.CompletionTokens = frame.Usage?.OutputTokens ?? state.CompletionTokens;
                    if (!string.IsNullOrWhiteSpace(frame.Delta?.StopReason))
                    {
                        state.SawStopReason = true;
                        state.StopReason = MapStopReason(frame.Delta.StopReason);
                    }
                    break;

                case "message_stop":
                    if (state.OpenContentBlocks.Count != 0)
                    {
                        yield return state.Error("Provider ended the message with an open content block.", state.Timer.ElapsedMilliseconds,
                            StudioAiStreamGrammarValidator.InvalidStreamCode);
                        yield break;
                    }

                    if (!state.SawStopReason)
                    {
                        yield return state.Error("Provider ended the stream without a stop reason.", state.Timer.ElapsedMilliseconds,
                            StudioAiStreamGrammarValidator.InvalidStreamCode);
                        yield break;
                    }

                    state.SawMessageStop = true;
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.MessageStop,
                        StopReason = state.StopReason,
                        PromptTokens = state.PromptTokens,
                        CompletionTokens = state.CompletionTokens,
                        LatencyMs = state.Timer.ElapsedMilliseconds
                    };
                    break;

                default:
                    yield return state.RejectFrame(frame, payload.Length, stoppedContentBlockType);
                    yield break;
            }
        }

        if (!state.SawMessageStop)
        {
            yield return state.Error("Provider stream ended before message_stop.", state.Timer.ElapsedMilliseconds);
        }
        else if (state.StopReason == StudioAiStopReason.ToolCall && !turn.CaptureLimitExceeded && !cancellationToken.IsCancellationRequested)
        {
            SaveContinuation(invocation, turn.Content(), credentialFingerprint);
        }
    }

    private void SaveContinuation(StudioAiHttpInvocation invocation, JsonElement content, string credentialFingerprint)
    {
        try
        {
            var scope = new AnthropicContinuationStore.Scope(invocation.CopyOptions(), invocation.Request, invocation.Model, invocation.ExecutionContext);
            var key = AnthropicContinuationStore.Key(scope, invocation.Request.Messages.Count, AnthropicAssistantTurn.VisibleMessage(content));
            _continuations.Save(key, new AnthropicContinuationStore.Turn(content, credentialFingerprint));
        }
        catch (JsonException)
        {
            // Private capture must not fail a visible turn that already completed.
        }
    }

    private AnthropicStreamFrame? ParseFrame(string payload)
    {
        try { return JsonSerializer.Deserialize(payload, StudioAiProxyJsonContext.Default.AnthropicStreamFrame); }
        catch (JsonException exception)
        {
            StudioAiProxyLog.ProviderResponseParseFailed(_logger, ProviderLabel, exception.GetType().Name);
            return null;
        }
    }

    private AnthropicProxyRequest BuildRequest(AnthropicContinuationStore.Scope scope)
    {
        var (options, request, model, _) = scope;
        var currentTurnStart = request.Messages.Select((message, index) => (message, index))
            .LastOrDefault(item => item.message.Role == StudioAiRole.User).index;
        var proxyRequest = new AnthropicProxyRequest
        {
            Model = model,
            MaxTokens = request.MaxTokens ?? options.MaxTokens,
            Temperature = request.Temperature,
            System = BuildSystemPrompt(request),
            Messages = request.Messages
                .Select((message, index) => (Message: message, Index: index))
                .Where(item => item.Message.Role != StudioAiRole.System)
                .Select(item => new AnthropicProxyMessage
                {
                    Role = item.Message.Role == StudioAiRole.Tool ? "user" : item.Message.Role.ToString().ToLowerInvariant(),
                    Content = RestoreMessageContent(scope, item.Index, currentTurnStart)
                })
                .ToArray()
        };

        var mode = request.ToolChoice?.Mode ?? StudioAiToolChoiceMode.Auto;
        if (request.Tools is { Count: > 0 } tools && mode != StudioAiToolChoiceMode.None)
        {
            proxyRequest.Tools = tools
                .Select(t => new AnthropicProxyTool { Name = t.Name, Description = t.BuildProviderDescription(), InputSchema = t.InputSchema })
                .ToArray();

            proxyRequest.ToolChoice = mode switch
            {
                StudioAiToolChoiceMode.Required => new AnthropicProxyToolChoice { Type = "any" },
                StudioAiToolChoiceMode.Specific => new AnthropicProxyToolChoice { Type = "tool", Name = request.ToolChoice!.ToolName },
                _ => new AnthropicProxyToolChoice { Type = "auto" }
            };
        }

        return proxyRequest;
    }

    private JsonElement RestoreMessageContent(AnthropicContinuationStore.Scope scope, int index, int currentTurnStart)
    {
        var message = scope.Request.Messages[index];
        if (message is not { Role: StudioAiRole.Assistant, ToolCalls.Count: > 0 }) return BuildMessageContent(message);
        var key = AnthropicContinuationStore.Key(scope, index, message);
        var turn = index >= currentTurnStart ? _continuations.Restore(key) : _continuations.TryRestore(key);
        if (turn is null) return BuildMessageContent(message);
        scope.RestoredCredentials.Add(turn.CredentialFingerprint);
        return turn.Content;
    }

    private static JsonElement BuildMessageContent(StudioAiMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (message.Role == StudioAiRole.Tool)
            {
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", message.ToolCallId);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            else if (message.Role == StudioAiRole.Assistant && message.ToolCalls is { Count: > 0 } toolCalls)
            {
                writer.WriteStartArray();
                if (!string.IsNullOrEmpty(message.Content))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", message.Content);
                    writer.WriteEndObject();
                }

                foreach (var call in toolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("input");
                    call.Arguments.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStringValue(message.Content);
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    // Anthropic rejects a system role inside messages, so system content is concatenated into the single top-level system field.
    private static string? BuildSystemPrompt(StudioAiChatRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            parts.Add(request.System);
        }

        parts.AddRange(request.Messages
            .Where(m => m.Role == StudioAiRole.System && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static StudioAiStopReason MapStopReason(string? stopReason) => stopReason switch
    {
        "tool_use" => StudioAiStopReason.ToolCall,
        "max_tokens" => StudioAiStopReason.MaxTokens,
        "refusal" => StudioAiStopReason.ContentFilter,
        _ => StudioAiStopReason.EndTurn
    };

    private static JsonElement? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class AnthropicStreamState(string model, ILogger<AnthropicStudioAiProxyAdapter> logger)
    {
        public (string? Error, string? StoppedType) TrackContentBlock(AnthropicStreamFrame frame)
        {
            if (frame.Type == "content_block_start"
                && (frame.Index is not { } startIndex || !OpenContentBlocks.TryAdd(startIndex, frame.ContentBlock?.Type ?? string.Empty)))
                return ("Provider returned an invalid content block start.", null);
            string? stoppedType = null;
            if (frame.Type == "content_block_stop"
                && (frame.Index is not { } stopIndex || !OpenContentBlocks.Remove(stopIndex, out stoppedType)))
                return ("Provider returned an unmatched content block stop.", null);
            return (null, stoppedType);
        }

        public Stopwatch Timer { get; } = new();
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public string? ToolCallId { get; set; }
        public StringBuilder? ToolArgsBuffer { get; set; }
        public StudioAiStopReason StopReason { get; set; } = StudioAiStopReason.EndTurn;
        public bool SawMessageStart { get; set; }
        public bool SawStopReason { get; set; }
        public bool SawMessageStop { get; set; }
        public Dictionary<int, string> OpenContentBlocks { get; } = new();

        public StudioAiChatEvent Error(string message, long? latencyMs = null, string? errorCode = null) => new()
        {
            Type = StudioAiChatEventType.Error,
            Model = model,
            ErrorCode = errorCode,
            ErrorMessage = message,
            LatencyMs = latencyMs,
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens
        };

        public StudioAiChatEvent RejectFrame(
            AnthropicStreamFrame frame, int payloadCharacters, string? stoppedContentBlockType)
        {
            var eventType = StreamMetadataLabel(frame.Type);
            var blockType = StreamMetadataLabel(frame.ContentBlock?.Type ?? stoppedContentBlockType
                ?? (frame.Index is { } index ? OpenContentBlocks.GetValueOrDefault(index) : null));
            var deltaType = StreamMetadataLabel(frame.Delta?.Type);
            var errorType = StreamMetadataLabel(frame.Error?.Type);
            var (errorCode, message) = frame.Type switch
            {
                "content_block_start" => (UnsupportedContentBlockCode,
                    $"Provider returned unsupported content block type '{blockType}'."),
                "error" => (ProviderStreamErrorCode, string.IsNullOrEmpty(frame.Error?.Code)
                    ? $"Provider stream error type '{errorType}'."
                    : $"Provider stream error type '{errorType}', code '{StreamMetadataLabel(frame.Error?.Code)}'."),
                "content_block_delta" or "content_block_stop" => (StudioAiStreamGrammarValidator.InvalidStreamCode,
                    $"Provider returned invalid stream event type '{eventType}' (delta type '{deltaType}', block type '{blockType}')."),
                _ => (StudioAiStreamGrammarValidator.InvalidStreamCode,
                    $"Provider returned unknown stream event type '{eventType}'.")
            };

            StudioAiProxyLog.ProviderStreamFrameRejected(
                logger, ProviderLabel, eventType, blockType, deltaType, errorType, payloadCharacters);
            return Error(message, Timer.ElapsedMilliseconds, errorCode);
        }
    }

    private static string StreamMetadataLabel(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "missing";
        }

        return value.Length <= MaxMetadataLabelLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value
            : "invalid";
    }
}
