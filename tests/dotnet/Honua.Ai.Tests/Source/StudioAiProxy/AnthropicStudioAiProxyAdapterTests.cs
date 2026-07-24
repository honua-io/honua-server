// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Streaming-parse contract tests for <see cref="AnthropicStudioAiProxyAdapter"/> against canned
/// Anthropic Messages API SSE fixtures — no live provider call. Covers text streaming, tool-call
/// start/delta/stop assembly, usage capture, and the HTTP-error / not-configured paths.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class AnthropicStudioAiProxyAdapterTests
{
    private const string TextTurnFixture = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":12,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string ToolCallFixture = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_2","model":"claude-sonnet-4-5","usage":{"input_tokens":20,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"list_incidents"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"status\":"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"open\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":8}}

        event: message_stop
        data: {"type":"message_stop"}

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
        stop.PromptTokens.Should().Be(12);
        stop.CompletionTokens.Should().Be(5);
    }

    [UnitTest]
    public async Task StreamAsync_ToolCallTurn_AssemblesFullArgumentsOnStop()
    {
        var adapter = CreateAdapter(ToolCallFixture);
        var events = await CollectAsync(adapter, ToolRequest());

        events.Select(e => e.Type).Should().Equal(
            StudioAiChatEventType.MessageStart,
            StudioAiChatEventType.ToolCallStart,
            StudioAiChatEventType.ToolCallDelta,
            StudioAiChatEventType.ToolCallDelta,
            StudioAiChatEventType.ToolCallStop,
            StudioAiChatEventType.MessageStop);

        var start = events[1];
        start.ToolCallId.Should().Be("toolu_1");
        start.ToolName.Should().Be("list_incidents");

        var stop = events[4];
        stop.ToolCallId.Should().Be("toolu_1");
        stop.ToolArguments.Should().NotBeNull();
        stop.ToolArguments!.Value.GetProperty("status").GetString().Should().Be("open");

        var messageStop = events[5];
        messageStop.StopReason.Should().Be(StudioAiStopReason.ToolCall);
        messageStop.PromptTokens.Should().Be(20);
        messageStop.CompletionTokens.Should().Be(8);
    }

    [UnitTest]
    public async Task StreamAsync_ProviderReturnsHttpError_EmitsSingleErrorEvent()
    {
        var adapter = CreateAdapter("provider is down", HttpStatusCode.InternalServerError);
        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Should().ContainSingle();
        events[0].Type.Should().Be(StudioAiChatEventType.Error);
        events[0].ErrorMessage.Should().Contain("500");
    }

    [UnitTest]
    public async Task StreamAsync_NoApiKeyConfigured_EmitsErrorWithoutCallingProvider()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        var options = new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.AnthropicKind,
            Endpoint = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5",
            ApiKey = string.Empty
        };

        var events = await CollectAsync(adapter, options, ToolFreeRequest());

        events.Should().ContainSingle();
        events[0].Type.Should().Be(StudioAiChatEventType.Error);
        handler.CapturedRequestBody.Should().BeNull("no HTTP call should be made without an API key");
    }

    [UnitTest]
    public void IsConfigured_RequiresEndpointAndModel()
    {
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(string.Empty)),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        adapter.Kind.Should().Be(StudioAiProxyConfiguration.AnthropicKind);
        adapter.IsConfigured(new StudioAiProxyProviderOptions { Endpoint = "https://api.anthropic.com", Model = "claude" }).Should().BeTrue();
        adapter.IsConfigured(new StudioAiProxyProviderOptions { Endpoint = "https://api.anthropic.com" }).Should().BeFalse();
        adapter.IsConfigured(new StudioAiProxyProviderOptions { Model = "claude" }).Should().BeFalse();
    }

    private static AnthropicStudioAiProxyAdapter CreateAdapter(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(responseBody, statusCode);
        return new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
    }

    private static async Task<List<StudioAiChatEvent>> CollectAsync(
        AnthropicStudioAiProxyAdapter adapter,
        StudioAiChatRequest request)
        => await CollectAsync(adapter, DefaultOptions(), request);

    private static async Task<List<StudioAiChatEvent>> CollectAsync(
        AnthropicStudioAiProxyAdapter adapter,
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
        Kind = StudioAiProxyConfiguration.AnthropicKind,
        Endpoint = "https://api.anthropic.com",
        Model = "claude-sonnet-4-5",
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
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "list open incidents" }],
        Tools =
        [
            new StudioAiToolDefinition
            {
                Name = "list_incidents",
                Description = "List open incidents.",
                InputSchema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"status":{"type":"string"}}}""").RootElement
            }
        ]
    };
}
