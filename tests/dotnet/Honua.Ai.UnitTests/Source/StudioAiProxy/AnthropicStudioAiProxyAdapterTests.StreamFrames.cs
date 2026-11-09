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
    private static readonly string[] _postTerminalFrames =
    {
        """{"type":"ping"}""",
        """{"type":"message_start","message":{"model":"claude-opus-5-5"}}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":99}}""",
        """{"type":"message_stop"}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text"}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tool_2","name":"example_tool"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"late"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"error","error":{"type":"overloaded_error","message":"private provider message"}}""",
        """{"type":"future_event","body":"private body"}""",
        """
        {"type":"content_block_start","index":0,"content_block":{"type":"text"}}
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""}}
        data: {"type":"content_block_stop","index":0}
        """
    };

    public static IEnumerable<object[]> PostTerminalStreams
        => _postTerminalFrames.Select(frame => new object[] { TextTurnFixture + "\ndata: " + frame + "\n" });

    [UnitTheory]
    [MemberData(nameof(PostTerminalStreams))]
    public async Task StreamAsync_FrameAfterMessageStop_RejectsBeforeConsumingIt(string fixture)
    {
        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var failure = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject;
        failure.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        failure.ErrorMessage.Should().Be("Provider returned a stream frame after message_stop.");
        failure.PromptTokens.Should().Be(12);
        failure.CompletionTokens.Should().Be(5);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStart);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.ToolCallStart || e.Text == "late");
    }

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

    [UnitTheory]
    [InlineData("", "missing")]
    [InlineData("data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\n", "text")]
    public async Task StreamAsync_EmptyTextDeltaBeforeMessageStart_FailsClosed(string prelude, string expectedBlockType)
    {
        var fixture = prelude + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"\"}}\n\n"
            + TextTurnFixture;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var failure = events.Should().ContainSingle().Subject;
        failure.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        failure.ErrorMessage.Should().Be($"Provider returned invalid stream event type 'content_block_delta' (delta type 'text_delta', block type '{expectedBlockType}').");
    }

    [UnitTest]
    public async Task StreamAsync_EmptyTextDeltaInToolBlock_FailsClosed()
    {
        var fixture = """
            data: {"type":"message_start","message":{"model":"claude-opus-5-5"}}
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tool_1","name":"example_tool"}}
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""}}
            data: {"type":"message_stop"}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var failure = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject;
        failure.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        failure.ErrorMessage.Should().Be("Provider returned invalid stream event type 'content_block_delta' (delta type 'text_delta', block type 'tool_use').");
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
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

        events.Should().ContainSingle().Which.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
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
        var fixture = $$$$"""
            data: {"type":"message_start","message":{"model":"claude-opus-5-5","usage":{"input_tokens":12,"output_tokens":1}}}
            data: {{{{frame}}}}
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
    [InlineData("{\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-5-5\"}}\n\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"citations_delta\",\"body\":\"PRIVATE_CANARY\"}}", "BlockType=text, DeltaType=citations_delta")]
    [InlineData("{\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-5-5\"}}\n\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"name\":\"example_tool\"}}\n\ndata: {\"type\":\"content_block_stop\",\"index\":0}", "BlockType=tool_use, DeltaType=missing")]
    public async Task StreamAsync_RejectedFrame_LogsOnlyBoundedMetadata(string providerFrames, string expectedMetadata)
    {
        var logger = Substitute.For<ILogger<AnthropicStudioAiProxyAdapter>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var clients = new StudioAiProxyMockHttpClientFactory(new StudioAiProxyMockHttpMessageHandler($"data: {providerFrames}\n\n"));
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(), logger);

        var events = await CollectAsync(adapter, ToolFreeRequest());

        var log = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").Should().ContainSingle().Subject;
        var arguments = log.GetArguments();
        ((EventId)arguments[1]!).Id.Should().Be(6757);
        arguments[2]!.ToString().Should().Contain(expectedMetadata).And.NotContain("PRIVATE_CANARY");
        arguments[3].Should().BeNull();
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject.ErrorMessage.Should().NotContain("PRIVATE_CANARY");
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTheory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task StreamAsync_UnknownEventMetadata_EnforcesLengthBoundary(int length, bool acceptedMetadata)
    {
        var eventType = new string('a', length);
        var frame = JsonSerializer.Serialize(new { type = eventType });

        var events = await CollectAsync(CreateAdapter($"data: {frame}\n\n"), ToolFreeRequest());

        var expectedLabel = acceptedMetadata ? eventType : "invalid";
        events.Should().ContainSingle().Which.ErrorMessage.Should()
            .Be($"Provider returned unknown stream event type '{expectedLabel}'.");
    }

    [UnitTheory]
    [InlineData("{\"type\":\"text_delta\",\"text\":null}", "text_delta")]
    [InlineData("{\"type\":\"citations_delta\",\"body\":\"private body\"}", "citations_delta")]
    [InlineData("{\"type\":\"input_json_delta\"}", "input_json_delta")]
    public async Task StreamAsync_KnownInvalidDelta_NamesDeltaAndOpenBlockType(string delta, string expectedDeltaType)
    {
        var fixture = """
            data: {"type":"message_start","message":{"model":"claude-opus-5-5"}}
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}
            """ + $"\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{delta}}}\n";

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var failure = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject;
        failure.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        failure.ErrorMessage.Should().Be($"Provider returned invalid stream event type 'content_block_delta' (delta type '{expectedDeltaType}', block type 'text').");
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTest]
    public async Task StreamAsync_ProviderErrorWithoutCode_OmitsCodeClause()
    {
        var fixture = """
            data: {"type":"error","error":{"type":"overloaded_error","message":"private provider message"}}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().ContainSingle().Which.ErrorMessage.Should().Be("Provider stream error type 'overloaded_error'.");
    }

    [UnitTest]
    public async Task StreamAsync_MessageDeltaWithoutUsage_PreservesMessageStartCounters()
    {
        var fixture = """
            data: {"type":"message_start","message":{"model":"claude-opus-5-5","usage":{"input_tokens":12,"output_tokens":1}}}
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}
            data: {"type":"message_stop"}
            """;

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        var completed = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.MessageStop).Subject;
        completed.PromptTokens.Should().Be(12);
        completed.CompletionTokens.Should().Be(1);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
    }
}
