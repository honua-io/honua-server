// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Streaming-parse contract tests for <see cref="OpenAiCompatibleStudioAiProxyAdapter"/> against
/// canned OpenAI-compatible <c>chat.completion.chunk</c> SSE fixtures — no live provider call. This
/// is the adapter kind that covers OpenAI itself, OpenRouter, LiteLLM, Ollama, and vLLM, so the
/// fixtures below use the wire shape common to all of them.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class OpenAiCompatibleStudioAiProxyAdapterTests
{
    private const string TextTurnFixture = """
        data: {"choices":[{"delta":{"role":"assistant"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":"Hello"}}]}

        data: {"choices":[{"delta":{"content":" world"}}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

        data: {"usage":{"prompt_tokens":10,"completion_tokens":4}}

        data: [DONE]

        """;

    // Two interleaved tool calls (indices 0 and 1) to prove the by-index accumulator does not
    // cross-contaminate arguments between them.
    private const string MultiToolCallFixture = """
        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"list_incidents","arguments":""}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_2","type":"function","function":{"name":"geocode","arguments":""}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"status\":"}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"address\":\"1 Main St\"}"}}]}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"open\"}"}}]}}]}

        data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

        data: {"usage":{"prompt_tokens":15,"completion_tokens":9}}

        data: [DONE]

        """;

    [UnitTest]
    public async Task StreamAsync_TextTurn_EmitsDeltasThenMessageStopWithUsage()
    {
        var adapter = CreateAdapter(TextTurnFixture);
        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Select(e => e.Type).Should().Equal(
            StudioAiChatEventType.MessageStart,
            StudioAiChatEventType.TextDelta,
            StudioAiChatEventType.TextDelta,
            StudioAiChatEventType.MessageStop);

        string.Concat(events.Where(e => e.Type == StudioAiChatEventType.TextDelta).Select(e => e.Text))
            .Should().Be("Hello world");

        var stop = events.Last();
        stop.StopReason.Should().Be(StudioAiStopReason.EndTurn);
        stop.PromptTokens.Should().Be(10);
        stop.CompletionTokens.Should().Be(4);
    }

    [UnitTest]
    public async Task StreamAsync_InterleavedToolCalls_AssembleIndependentlyByIndex()
    {
        var adapter = CreateAdapter(MultiToolCallFixture);
        var events = await CollectAsync(adapter, ToolRequest());

        var stops = events.Where(e => e.Type == StudioAiChatEventType.ToolCallStop).ToList();
        stops.Should().HaveCount(2);

        var incidentsCall = stops.Single(s => s.ToolCallId == "call_1");
        incidentsCall.ToolArguments!.Value.GetProperty("status").GetString().Should().Be("open");

        var geocodeCall = stops.Single(s => s.ToolCallId == "call_2");
        geocodeCall.ToolArguments!.Value.GetProperty("address").GetString().Should().Be("1 Main St");

        var messageStop = events.Last();
        messageStop.Type.Should().Be(StudioAiChatEventType.MessageStop);
        messageStop.StopReason.Should().Be(StudioAiStopReason.ToolCall);
    }

    [UnitTest]
    public async Task StreamAsync_ProviderReturnsHttpError_EmitsSingleErrorEvent()
    {
        var adapter = CreateAdapter("bad gateway", HttpStatusCode.BadGateway);
        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Should().ContainSingle();
        events[0].Type.Should().Be(StudioAiChatEventType.Error);
        events[0].ErrorMessage.Should().Contain("502");
    }

    [UnitTest]
    public async Task StreamAsync_LocalEndpointWithNoApiKey_StillCallsProvider()
    {
        // A local Ollama/vLLM endpoint typically needs no key; absence must not block the call the
        // way it does for the Anthropic adapter.
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new OpenAiCompatibleStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance);

        var options = new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.OpenAiKind,
            Endpoint = "http://localhost:8000/v1",
            Model = "local-model",
            ApiKey = string.Empty
        };

        var events = await CollectAsync(adapter, options, ToolFreeRequest());

        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        handler.CapturedRequestBody.Should().NotBeNull();
    }

    [UnitTest]
    public async Task StreamAsync_SpecificToolChoiceAndToolResult_UsesOpenAiWireShape()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new OpenAiCompatibleStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance);
        var request = new StudioAiChatRequest
        {
            Messages =
            [
                new StudioAiMessage
                {
                    Role = StudioAiRole.Tool,
                    Content = """{"status":"open"}""",
                    ToolCallId = "call_123",
                    ToolName = "list_incidents"
                }
            ],
            Tools = ToolRequest().Tools,
            ToolChoice = new StudioAiToolChoice
            {
                Mode = StudioAiToolChoiceMode.Specific,
                ToolName = "list_incidents"
            }
        };

        await CollectAsync(adapter, request);

        using var payload = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = payload.RootElement;
        root.GetProperty("messages")[0].GetProperty("tool_call_id").GetString().Should().Be("call_123");
        root.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("function");
        root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString()
            .Should().Be("list_incidents");
    }

    [UnitTest]
    public void IsConfigured_RequiresEndpointAndModel()
    {
        var adapter = new OpenAiCompatibleStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(string.Empty)),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance);

        adapter.Kind.Should().Be(StudioAiProxyConfiguration.OpenAiKind);
        adapter.IsConfigured(new StudioAiProxyProviderOptions { Endpoint = "http://localhost:8000/v1", Model = "m" }).Should().BeTrue();
        adapter.IsConfigured(new StudioAiProxyProviderOptions { Endpoint = "http://localhost:8000/v1" }).Should().BeFalse();
    }

    private static OpenAiCompatibleStudioAiProxyAdapter CreateAdapter(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(responseBody, statusCode);
        return new OpenAiCompatibleStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<OpenAiCompatibleStudioAiProxyAdapter>.Instance);
    }

    private static Task<List<StudioAiChatEvent>> CollectAsync(
        OpenAiCompatibleStudioAiProxyAdapter adapter,
        StudioAiChatRequest request)
        => CollectAsync(adapter, DefaultOptions(), request);

    private static async Task<List<StudioAiChatEvent>> CollectAsync(
        OpenAiCompatibleStudioAiProxyAdapter adapter,
        StudioAiProxyProviderOptions options,
        StudioAiChatRequest request)
    {
        List<StudioAiChatEvent> events = [];
        await foreach (var evt in adapter.StreamAsync(options, request, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    private static StudioAiProxyProviderOptions DefaultOptions() => new()
    {
        Kind = StudioAiProxyConfiguration.OpenAiKind,
        Endpoint = "https://openrouter.ai/api/v1",
        Model = "anthropic/claude-sonnet-4.5",
        ApiKey = "test-key",
        MaxTokens = 1024,
        TimeoutSeconds = 30
    };

    private static StudioAiChatRequest ToolFreeRequest() => new()
    {
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
    };

    private static StudioAiChatRequest ToolRequest() => new()
    {
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "list open incidents and geocode 1 Main St" }],
        Tools =
        [
            new StudioAiToolDefinition
            {
                Name = "list_incidents",
                InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"status":{"type":"string"}}}""").RootElement
            },
            new StudioAiToolDefinition
            {
                Name = "geocode",
                InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"address":{"type":"string"}}}""").RootElement
            }
        ]
    };
}
