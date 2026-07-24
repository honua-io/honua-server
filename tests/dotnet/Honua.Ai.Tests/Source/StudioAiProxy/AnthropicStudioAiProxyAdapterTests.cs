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
    public async Task StreamAsync_AssistantToolCallAndResult_UsesAnthropicWireShape()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var request = new StudioAiChatRequest
        {
            Messages =
            [
                new StudioAiMessage
                {
                    Role = StudioAiRole.Assistant,
                    Content = string.Empty,
                    ToolCalls =
                    [
                        new StudioAiToolCall
                        {
                            Id = "toolu_123",
                            Name = "list_incidents",
                            Arguments = JsonDocument.Parse("""{"status":"open"}""").RootElement.Clone()
                        }
                    ]
                },
                new StudioAiMessage
                {
                    Role = StudioAiRole.Tool,
                    Content = """[{"id":1}]""",
                    ToolCallId = "toolu_123",
                    ToolName = "list_incidents"
                }
            ],
            Tools = ToolRequest().Tools
        };

        await CollectAsync(adapter, request);

        using var payload = JsonDocument.Parse(handler.CapturedRequestBody!);
        var messages = payload.RootElement.GetProperty("messages");
        var replayedCall = messages[0].GetProperty("content")[0];
        replayedCall.GetProperty("type").GetString().Should().Be("tool_use");
        replayedCall.GetProperty("id").GetString().Should().Be("toolu_123");
        replayedCall.GetProperty("input").GetProperty("status").GetString().Should().Be("open");
        var result = messages[1].GetProperty("content")[0];
        result.GetProperty("type").GetString().Should().Be("tool_result");
        result.GetProperty("tool_use_id").GetString().Should().Be("toolu_123");
    }

    [UnitTest]
    public void IsConfigured_RequiresEndpointModelAndApiKey()
    {
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(string.Empty)),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        adapter.Kind.Should().Be(StudioAiProxyConfiguration.AnthropicKind);
        adapter.IsConfigured("claude", new StudioAiProxyProviderOptions { Endpoint = "https://api.anthropic.com", Model = "claude", ApiKey = "key" }).Should().BeTrue();
        adapter.IsConfigured("claude", new StudioAiProxyProviderOptions { Endpoint = "https://api.anthropic.com", ApiKey = "key" }).Should().BeFalse("Model is missing");
        adapter.IsConfigured("claude", new StudioAiProxyProviderOptions { Model = "claude", ApiKey = "key" }).Should().BeFalse("Endpoint is missing");
    }

    [UnitTest]
    public void IsConfigured_EndpointAndModelPresent_ButNoApiKeyAndNoEnvFallback_ReturnsFalse()
    {
        // honua-server#3010 review: a provider declared without ApiKey and without the per-provider
        // env var fallback can never actually be called, so it must not report configured — otherwise
        // GET /capabilities lies and POST /chat commits a 200 SSE stream before erroring.
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(string.Empty)),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        var options = new StudioAiProxyProviderOptions { Endpoint = "https://api.anthropic.com", Model = "claude", ApiKey = string.Empty };

        adapter.IsConfigured("keyless-anthropic-3010-test", options).Should().BeFalse();
    }

    [UnitTest]
    public void IsConfigured_NoApiKeyButPerProviderEnvVarFallbackSet_ReturnsTrue()
    {
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(string.Empty)),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        var options = new StudioAiProxyProviderOptions { Endpoint = "https://api.anthropic.com", Model = "claude", ApiKey = string.Empty };
        const string providerName = "env-fallback-anthropic-3010-test";
        var envVarName = StudioAiProxyApiKeyResolver.EnvVarName(providerName);

        Environment.SetEnvironmentVariable(envVarName, "from-env");
        try
        {
            adapter.IsConfigured(providerName, options).Should().BeTrue("the per-provider environment variable is a valid credential source");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [UnitTest]
    public async Task StreamAsync_TopLevelSystemField_RoundTripsToAnthropicSystemParam()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        var request = new StudioAiChatRequest
        {
            System = "You are a GIS analyst.",
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
        };

        await CollectAsync(adapter, DefaultOptions(), request);

        handler.CapturedRequestBody.Should().Contain("\"system\":\"You are a GIS analyst.\"");
        // Anthropic's Messages API has no "system" role in messages[]; the top-level field is the
        // only carrier, so no message in the wire payload should be role "system".
        handler.CapturedRequestBody.Should().NotContain("\"role\":\"system\"");
    }

    [UnitTest]
    public async Task StreamAsync_SystemRoleMessage_IsFoldedIntoAnthropicSystemParamNotDropped()
    {
        // honua-server#3010 review: the mapper accepts a messages[] entry with role=system (the
        // OpenAI adapter forwards it as an OpenAI "system"-role message), but Anthropic's Messages
        // API has no such role -- it must be folded into the top-level `system` string instead of
        // silently disappearing.
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        var request = new StudioAiChatRequest
        {
            Messages =
            [
                new StudioAiMessage { Role = StudioAiRole.System, Content = "Be terse." },
                new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }
            ]
        };

        await CollectAsync(adapter, DefaultOptions(), request);

        handler.CapturedRequestBody.Should().Contain("\"system\":\"Be terse.\"");
        handler.CapturedRequestBody.Should().NotContain("\"role\":\"system\"", "Anthropic rejects a system role inside messages[]");
    }

    [UnitTest]
    public async Task StreamAsync_TopLevelSystemAndSystemRoleMessage_AreConcatenatedInOrder()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(TextTurnFixture);
        var adapter = new AnthropicStudioAiProxyAdapter(
            new StudioAiProxyMockHttpClientFactory(handler),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);

        var request = new StudioAiChatRequest
        {
            System = "You are a GIS analyst.",
            Messages =
            [
                new StudioAiMessage { Role = StudioAiRole.System, Content = "Be terse." },
                new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }
            ]
        };

        await CollectAsync(adapter, DefaultOptions(), request);

        handler.CapturedRequestBody.Should().Contain("\"system\":\"You are a GIS analyst.\\n\\nBe terse.\"");
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
