// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

public sealed partial class AnthropicStudioAiProxyAdapterTests
{
    [UnitTest]
    public async Task StreamAsync_EmptyTextDelta_IgnoresItAndPreservesCompletionAndUsage()
    {
        var fixture = """
            data: {"type":"message_start","message":{"model":"claude-opus-5-5","usage":{"input_tokens":12}}}
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}
            data: {"type":"content_block_stop","index":0}
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}
            data: {"type":"message_stop"}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Where(e => e.Type == StudioAiChatEventType.TextDelta).Should().ContainSingle().Which.Text.Should().Be("Hello");
        var completed = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStop).Subject;
        completed.PromptTokens.Should().Be(12);
        completed.CompletionTokens.Should().Be(2);
    }

    [UnitTheory]
    [InlineData("thinking")]
    [InlineData("server_tool_use")]
    public async Task StreamAsync_UnsupportedContentBlock_NamesTypeAndFailsClosed(string blockType)
    {
        var fixture = $$$"""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"{{{blockType}}}","thinking":"private body"}}
            data: {"type":"message_stop"}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().ContainSingle().Which.ErrorCode.Should().Be("studio_ai/unsupported_content_block");
        events[0].ErrorMessage.Should().Be($"Provider returned unsupported content block type '{blockType}'.");
    }

    [UnitTest]
    public async Task StreamAsync_ProviderErrorFrame_SurfacesTypeAndCodeWithoutMessage()
    {
        var fixture = """
            data: {"type":"message_start","message":{"usage":{"input_tokens":12,"output_tokens":1}}}
            data: {"type":"message_delta","usage":{"output_tokens":2}}
            data: {"type":"error","error":{"type":"overloaded_error","code":"capacity","message":"private provider message"}}
            data: {"type":"message_stop"}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var failure = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject;
        failure.ErrorCode.Should().Be("studio_ai/provider_error");
        failure.ErrorMessage.Should().Be("Provider stream error type 'overloaded_error', code 'capacity'.");
        failure.PromptTokens.Should().Be(12);
        failure.CompletionTokens.Should().Be(2);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTheory]
    [InlineData("{\"type\":\"future_event\",\"body\":\"private body\"}", "future_event")]
    [InlineData("{}", "missing")]
    [InlineData("{\"type\":null}", "missing")]
    public async Task StreamAsync_UnknownEvent_NamesEventAndFailsClosed(string frame, string eventType)
    {
        var events = await CollectAsync(CreateAdapter($"data: {frame}\n\n"), ToolFreeRequest());

        events.Should().ContainSingle().Which.ErrorCode.Should().Be("studio_ai/unknown_provider_event");
        events[0].ErrorMessage.Should().Be($"Provider returned unknown stream event type '{eventType}'.");
        events[0].PromptTokens.Should().BeNull();
        events[0].CompletionTokens.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\"}}")]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}")]
    [InlineData("{\"type\":\"future_event\"}")]
    public async Task StreamAsync_RejectedFrame_PreservesUsageFromMessageStart(string frame)
    {
        var fixture = $$$"""
            data: {"type":"message_start","message":{"model":"claude-opus-5-5","usage":{"input_tokens":12,"output_tokens":1}}}
            data: {{{frame}}}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var failure = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject;
        failure.PromptTokens.Should().Be(12);
        failure.CompletionTokens.Should().Be(1);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTheory]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"PRIVATE_CANARY\"}}", "overloaded_error")]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"PRIVATE_CANARY\\n\",\"code\":\"https://PRIVATE_CANARY\"}}", "invalid")]
    [InlineData("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"PRIVATE_CANARY\"}}", "thinking")]
    [InlineData("{\"type\":\"PRIVATE_CANARY\\n\",\"body\":\"PRIVATE_CANARY\"}", "invalid")]
    public async Task StreamAsync_RejectedFrame_LogsOnlyBoundedMetadata(string frame, string expectedMetadata)
    {
        var logger = Substitute.For<ILogger<AnthropicStudioAiProxyAdapter>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var clients = new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler($"data: {frame}\n\n"));
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(), logger);

        var events = await CollectAsync(adapter, ToolFreeRequest());

        var log = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").Should().ContainSingle().Subject;
        var arguments = log.GetArguments();
        arguments[2]!.ToString().Should().Contain(expectedMetadata).And.NotContain("PRIVATE_CANARY");
        arguments[3].Should().BeNull();
        events.Single().ErrorMessage.Should().NotContain("PRIVATE_CANARY");
    }
}
