// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
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
    internal const string ThinkingBlockFixture = """
        data: {"type":"content_block_start","index":7,"content_block":{"type":"thinking","thinking":"PRIVATE_THINKING","signature":""}}
        data: {"type":"content_block_delta","index":7,"delta":{"type":"thinking_delta","thinking":"PRIVATE_THINKING"}}
        data: {"type":"content_block_delta","index":7,"delta":{"type":"signature_delta","signature":"PRIVATE_SIGNATURE"}}
        data: {"type":"content_block_stop","index":7}

        """;

    internal static string WithThinking(string fixture)
        => fixture.Replace("event: content_block_start", ThinkingBlockFixture + "event: content_block_start", StringComparison.Ordinal);

    internal static string InterleavedThinkingFixture => WithThinking(
        TextTurnFixture[..TextTurnFixture.IndexOf("event: message_delta", StringComparison.Ordinal)]
        + "\n" + ToolCallFixture[ToolCallFixture.IndexOf("event: content_block_start", StringComparison.Ordinal)..]);

    [UnitTheory]
    [InlineData(TextTurnFixture)]
    [InlineData(ToolCallFixture)]
    public async Task StreamAsync_ThinkingBeforeTextOrTool_PreservesVisibleEventsAndUsage(string fixture)
    {
        var request = ToolRequest();
        var expected = await CollectAsync(CreateAdapter(fixture), request);

        var events = await CollectAsync(CreateAdapter(WithThinking(fixture)), request);

        events.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering()
            .Excluding(e => e.LatencyMs).Excluding(e => e.ToolArguments));
        events.Select(e => e.ToolArguments?.GetRawText()).Should().Equal(expected.Select(e => e.ToolArguments?.GetRawText()));
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_THINKING").And.NotContain("PRIVATE_SIGNATURE");
    }

    [UnitTheory]
    [InlineData("thinking_delta")]
    [InlineData("signature_delta")]
    public async Task StreamAsync_ThinkingDeltaOutsideThinkingBlock_RejectsInvalidGrammar(string deltaType)
    {
        var fixture = TextTurnFixture.Replace("\"type\":\"text_delta\"", $"\"type\":\"{deltaType}\"", StringComparison.Ordinal);

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject.ErrorCode.Should()
            .Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTest]
    public async Task StreamAsync_ThinkingInterleavedWithTextAndTool_PreservesBothOutputs()
    {
        var events = await CollectAsync(CreateAdapter(InterleavedThinkingFixture), ToolRequest());

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        string.Concat(events.Where(e => e.Type == StudioAiChatEventType.TextDelta).Select(e => e.Text)).Should().Be("Hello world");
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop).Subject
            .ToolArguments!.Value.GetProperty("status").GetString().Should().Be("open");
        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        events.Last().CompletionTokens.Should().Be(8);
    }

    [UnitTheory]
    [InlineData("\"index\":7,\"delta\"", "\"index\":8,\"delta\"")]
    [InlineData("\"type\":\"content_block_stop\",\"index\":7", "\"type\":\"content_block_stop\",\"index\":8")]
    [InlineData("data: {\"type\":\"content_block_stop\",\"index\":7}", "")]
    public async Task StreamAsync_ThinkingWithInvalidLifecycle_RejectsStream(string original, string replacement)
    {
        var fixture = WithThinking(TextTurnFixture).Replace(original, replacement, StringComparison.Ordinal);

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject.ErrorCode.Should()
            .Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTheory]
    [InlineData("{\"type\":\"text_delta\",\"text\":\"PRIVATE_THINKING\"}")]
    [InlineData("{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}")]
    [InlineData("{\"type\":\"future_delta\"}")]
    public async Task StreamAsync_ThinkingBlockWithWrongDelta_RejectsWithoutExposingContent(string delta)
    {
        var fixture = WithThinking(TextTurnFixture).Replace(
            "{\"type\":\"thinking_delta\",\"thinking\":\"PRIVATE_THINKING\"}", delta, StringComparison.Ordinal);

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject.ErrorCode.Should()
            .Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_THINKING");
    }

    [UnitTest]
    public async Task StreamAsync_ThinkingStream_DoesNotLogReasoningOrSignature()
    {
        var logger = Substitute.For<ILogger<AnthropicStudioAiProxyAdapter>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var clients = new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler(WithThinking(TextTurnFixture)));
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(), logger);

        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").Should().BeEmpty();
    }
}
