// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters.Models;
using Honua.Ai.StudioAiProxy.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Ai.StudioAiProxy.Adapters;

/// <summary>
/// Adapter for the Anthropic Messages API (<c>POST {endpoint}/v1/messages</c>, streaming). Maps
/// Anthropic's SSE frame vocabulary (<c>message_start</c>/<c>content_block_*</c>/<c>message_delta</c>/
/// <c>message_stop</c>) onto the provider-neutral <see cref="StudioAiChatEvent"/> sequence.
/// </summary>
internal sealed class AnthropicStudioAiProxyAdapter : IStudioAiProxyAdapter
{
    private const string AnthropicVersion = "2023-06-01";
    private const string ProviderLabel = "anthropic";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StudioAiProxyApiKeyResolver _apiKeyResolver;
    private readonly ILogger<AnthropicStudioAiProxyAdapter> _logger;

    public AnthropicStudioAiProxyAdapter(
        IHttpClientFactory httpClientFactory,
        StudioAiProxyApiKeyResolver apiKeyResolver,
        ILogger<AnthropicStudioAiProxyAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKeyResolver = apiKeyResolver;
        _logger = logger;
    }

    public string Kind => StudioAiProxyConfiguration.AnthropicKind;

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
        var apiKey = await _apiKeyResolver.ResolveAsync(ProviderLabel, options, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return Error(model, "Anthropic API key is not configured.");
            yield break;
        }

        var proxyRequest = BuildRequest(options, request, model);

        var client = _httpClientFactory.CreateClient("studio-ai-proxy");
        var endpoint = options.Endpoint.TrimEnd('/') + "/v1/messages";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(proxyRequest, StudioAiProxyJsonContext.Default.AnthropicProxyRequest),
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

        string? toolCallId = null;
        StringBuilder? toolArgsBuffer = null;
        int? promptTokens = null;
        int? completionTokens = null;
        var stopReason = StudioAiStopReason.EndTurn;
        var sawMessageStop = false;

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

            AnthropicStreamFrame? frame = null;
            var parseFailed = false;
            try
            {
                frame = JsonSerializer.Deserialize(payload, StudioAiProxyJsonContext.Default.AnthropicStreamFrame);
            }
            catch (JsonException ex)
            {
                parseFailed = true;
                StudioAiProxyLog.ProviderResponseParseFailed(_logger, ProviderLabel, ex);
            }

            if (parseFailed || frame is null)
            {
                continue;
            }

            switch (frame.Type)
            {
                case "message_start":
                    promptTokens = frame.Message?.Usage?.InputTokens ?? promptTokens;
                    break;

                case "content_block_start" when frame.ContentBlock?.Type == "tool_use":
                    toolCallId = frame.ContentBlock.Id;
                    toolArgsBuffer = new StringBuilder();
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.ToolCallStart,
                        ToolCallId = toolCallId,
                        ToolName = frame.ContentBlock.Name
                    };
                    break;

                case "content_block_delta" when frame.Delta?.Type == "text_delta" && frame.Delta.Text is { Length: > 0 } text:
                    yield return new StudioAiChatEvent { Type = StudioAiChatEventType.TextDelta, Text = text };
                    break;

                case "content_block_delta" when frame.Delta?.Type == "input_json_delta" && frame.Delta.PartialJson is { } partial:
                    toolArgsBuffer?.Append(partial);
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.ToolCallDelta,
                        ToolCallId = toolCallId,
                        ToolArgumentsDelta = partial
                    };
                    break;

                case "content_block_stop" when toolCallId is not null:
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.ToolCallStop,
                        ToolCallId = toolCallId,
                        ToolArguments = TryParse(toolArgsBuffer?.ToString())
                    };
                    toolCallId = null;
                    toolArgsBuffer = null;
                    break;

                case "message_delta":
                    completionTokens = frame.Usage?.OutputTokens ?? completionTokens;
                    stopReason = MapStopReason(frame.Delta?.StopReason);
                    break;

                case "message_stop":
                    sawMessageStop = true;
                    yield return new StudioAiChatEvent
                    {
                        Type = StudioAiChatEventType.MessageStop,
                        StopReason = stopReason,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        LatencyMs = stopwatch.ElapsedMilliseconds
                    };
                    break;
            }
        }

        if (!sawMessageStop)
        {
            yield return Error(model, "Provider stream ended before message_stop.", stopwatch.ElapsedMilliseconds);
        }
    }

    private static AnthropicProxyRequest BuildRequest(
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request,
        string model)
    {
        var proxyRequest = new AnthropicProxyRequest
        {
            Model = model,
            MaxTokens = request.MaxTokens ?? options.MaxTokens,
            Temperature = request.Temperature,
            System = request.System,
            Messages = request.Messages
                .Where(m => m.Role != StudioAiRole.System)
                .Select(m => new AnthropicProxyMessage
                {
                    Role = m.Role == StudioAiRole.Tool ? "user" : m.Role.ToString().ToLowerInvariant(),
                    Content = m.Content
                })
                .ToArray()
        };

        var mode = request.ToolChoice?.Mode ?? StudioAiToolChoiceMode.Auto;
        if (request.Tools is { Count: > 0 } tools && mode != StudioAiToolChoiceMode.None)
        {
            proxyRequest.Tools = tools
                .Select(t => new AnthropicProxyTool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema })
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

    private static StudioAiChatEvent Error(string model, string message, long? latencyMs = null) => new()
    {
        Type = StudioAiChatEventType.Error,
        Model = model,
        ErrorMessage = message,
        LatencyMs = latencyMs
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
