// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.Ai.StudioAiProxy.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.StudioAiProxy.Adapters;

/// <summary>
/// Adapter for any OpenAI-compatible <c>POST {endpoint}/chat/completions</c> streaming endpoint —
/// covers OpenAI itself, OpenRouter, LiteLLM, Ollama, vLLM, and LM Studio as operator config choices
/// (they all speak this wire shape; only <see cref="StudioAiProxyProviderOptions.Endpoint"/> and
/// whether an API key is required differ).
/// </summary>
internal sealed class OpenAiCompatibleStudioAiProxyAdapter : IStudioAiProxyAdapter
{
    private const string ProviderLabel = "openai";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StudioAiProxyApiKeyResolver _apiKeyResolver;
    private readonly ILogger<OpenAiCompatibleStudioAiProxyAdapter> _logger;

    public OpenAiCompatibleStudioAiProxyAdapter(
        IHttpClientFactory httpClientFactory,
        StudioAiProxyApiKeyResolver apiKeyResolver,
        ILogger<OpenAiCompatibleStudioAiProxyAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKeyResolver = apiKeyResolver;
        _logger = logger;
    }

    public string Kind => StudioAiProxyConfiguration.OpenAiKind;

    public bool IsConfigured(StudioAiProxyProviderOptions options)
        => !string.IsNullOrWhiteSpace(options.Endpoint) && !string.IsNullOrWhiteSpace(options.Model);

    public async IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model!;
        // A local endpoint (Ollama/vLLM/LM Studio) typically needs no key; a hosted gateway does.
        // Absence here is not itself a configuration error — the provider will reject the call with
        // an auth error if a key really was required, and that surfaces as a normal Error event.
        var apiKey = await _apiKeyResolver.ResolveAsync(ProviderLabel, options, cancellationToken).ConfigureAwait(false);

        var proxyRequest = BuildRequest(options, request, model);

        var client = _httpClientFactory.CreateClient("studio-ai-proxy");
        var endpoint = options.Endpoint.TrimEnd('/') + "/chat/completions";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(proxyRequest, StudioAiProxyJsonContext.Default.OpenAiProxyRequest),
            Encoding.UTF8,
            "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();

        var (response, sendTimedOut, requestError) = await StudioAiProxyHttpStreamHelpers
            .SendSafeAsync(client, httpRequest, timeoutCts.Token, cancellationToken)
            .ConfigureAwait(false);

        if (sendTimedOut)
        {
            StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
            yield return Error(model, "Provider request timed out.");
            yield break;
        }

        if (requestError is not null)
        {
            StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, requestError);
            yield return Error(model, "Provider request failed.");
            yield break;
        }

        using var successResponse = response!;
        if (!successResponse.IsSuccessStatusCode)
        {
            var body = await successResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            StudioAiProxyLog.ProviderHttpError(_logger, ProviderLabel, (int)successResponse.StatusCode, Truncate(body, 500));
            yield return Error(model, $"Provider returned HTTP {(int)successResponse.StatusCode}.");
            yield break;
        }

        await using var stream = await successResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        yield return new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = model };

        // Multiple tool calls can interleave within one streamed turn, each identified by its
        // "index" in the delta payload; the first delta for an index carries id+name, later deltas
        // for the same index only carry argument fragments.
        var toolCalls = new Dictionary<int, ToolCallAccumulator>();
        int? promptTokens = null;
        int? completionTokens = null;
        var stopReason = StudioAiStopReason.EndTurn;
        var sawDone = false;
        var sawFinishReason = false;

        while (true)
        {
            var (line, readTimedOut, ioError) = await StudioAiProxyHttpStreamHelpers
                .ReadLineSafeAsync(reader, timeoutCts.Token, cancellationToken)
                .ConfigureAwait(false);

            if (readTimedOut)
            {
                StudioAiProxyLog.ProviderTimeout(_logger, ProviderLabel);
                yield return Error(model, "Provider request timed out.", stopwatch.ElapsedMilliseconds);
                yield break;
            }

            if (ioError is not null)
            {
                StudioAiProxyLog.ProviderRequestFailed(_logger, ProviderLabel, ioError);
                yield return Error(model, "Provider stream ended unexpectedly.", stopwatch.ElapsedMilliseconds);
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

            if (payload == "[DONE]")
            {
                sawDone = true;
                break;
            }

            OpenAiStreamChunk? chunk = null;
            var parseFailed = false;
            try
            {
                chunk = JsonSerializer.Deserialize(payload, StudioAiProxyJsonContext.Default.OpenAiStreamChunk);
            }
            catch (JsonException ex)
            {
                parseFailed = true;
                StudioAiProxyLog.ProviderResponseParseFailed(_logger, ProviderLabel, ex);
            }

            if (parseFailed || chunk is null)
            {
                continue;
            }

            if (chunk.Usage is { } usage)
            {
                promptTokens = usage.PromptTokens;
                completionTokens = usage.CompletionTokens;
            }

            var choice = chunk.Choices is { Length: > 0 } choices ? choices[0] : null;
            if (choice?.Delta?.Content is { Length: > 0 } text)
            {
                yield return new StudioAiChatEvent { Type = StudioAiChatEventType.TextDelta, Text = text };
            }

            if (choice?.Delta?.ToolCalls is { Length: > 0 } toolCallDeltas)
            {
                foreach (var delta in toolCallDeltas)
                {
                    if (!toolCalls.TryGetValue(delta.Index, out var accumulator))
                    {
                        accumulator = new ToolCallAccumulator(delta.Id ?? $"call_{delta.Index}", delta.Function?.Name ?? string.Empty);
                        toolCalls[delta.Index] = accumulator;
                        yield return new StudioAiChatEvent
                        {
                            Type = StudioAiChatEventType.ToolCallStart,
                            ToolCallId = accumulator.Id,
                            ToolName = accumulator.Name
                        };
                    }

                    if (delta.Function?.Arguments is { Length: > 0 } argsFragment)
                    {
                        accumulator.Append(argsFragment);
                        yield return new StudioAiChatEvent
                        {
                            Type = StudioAiChatEventType.ToolCallDelta,
                            ToolCallId = accumulator.Id,
                            ToolArgumentsDelta = argsFragment
                        };
                    }
                }
            }

            if (!string.IsNullOrEmpty(choice?.FinishReason))
            {
                sawFinishReason = true;
                stopReason = MapStopReason(choice!.FinishReason!);
            }
        }

        foreach (var accumulator in toolCalls.Values)
        {
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.ToolCallStop,
                ToolCallId = accumulator.Id,
                ToolArguments = accumulator.TryParse()
            };
        }

        if (!sawFinishReason && !sawDone)
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

    private static OpenAiProxyRequest BuildRequest(
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request,
        string model)
    {
        List<OpenAiProxyMessage> messages = [];
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new OpenAiProxyMessage { Role = "system", Content = request.System });
        }

        messages.AddRange(request.Messages.Select(m => new OpenAiProxyMessage
        {
            Role = m.Role switch
            {
                StudioAiRole.System => "system",
                StudioAiRole.Assistant => "assistant",
                StudioAiRole.Tool => "tool",
                _ => "user"
            },
            Content = m.Content,
            ToolCallId = m.Role == StudioAiRole.Tool ? m.ToolCallId : null
        }));

        var proxyRequest = new OpenAiProxyRequest
        {
            Model = model,
            Messages = [.. messages],
            MaxTokens = request.MaxTokens ?? options.MaxTokens,
            Temperature = request.Temperature,
            StreamOptions = new OpenAiProxyStreamOptions { IncludeUsage = true }
        };

        var mode = request.ToolChoice?.Mode ?? StudioAiToolChoiceMode.Auto;
        if (request.Tools is { Count: > 0 } tools && mode != StudioAiToolChoiceMode.None)
        {
            proxyRequest.Tools = tools
                .Select(t => new OpenAiProxyTool
                {
                    Function = new OpenAiProxyFunction { Name = t.Name, Description = t.Description, Parameters = t.InputSchema }
                })
                .ToArray();

            proxyRequest.ToolChoice = mode switch
            {
                StudioAiToolChoiceMode.Required => BuildToolChoice("required", toolName: null),
                StudioAiToolChoiceMode.Specific => BuildToolChoice("specific", request.ToolChoice!.ToolName),
                _ => BuildToolChoice("auto", toolName: null)
            };
        }

        return proxyRequest;
    }

    private static JsonElement BuildToolChoice(string mode, string? toolName)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (mode == "specific")
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", toolName);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStringValue(mode);
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static StudioAiStopReason MapStopReason(string finishReason) => finishReason switch
    {
        "tool_calls" => StudioAiStopReason.ToolCall,
        "length" => StudioAiStopReason.MaxTokens,
        "content_filter" => StudioAiStopReason.ContentFilter,
        _ => StudioAiStopReason.EndTurn
    };

    private static StudioAiChatEvent Error(string model, string message, long? latencyMs = null) => new()
    {
        Type = StudioAiChatEventType.Error,
        Model = model,
        ErrorMessage = message,
        LatencyMs = latencyMs
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Accumulates one tool call's streamed argument fragments, keyed by delta index.</summary>
    private sealed class ToolCallAccumulator(string id, string name)
    {
        private readonly StringBuilder _arguments = new();

        public string Id { get; } = id;

        public string Name { get; } = name;

        public void Append(string fragment) => _arguments.Append(fragment);

        public JsonElement? TryParse()
        {
            var json = _arguments.ToString();
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
    }
}
