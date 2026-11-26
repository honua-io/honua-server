// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Admission;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

public sealed partial class AnthropicStudioAiProxyAdapterTests
{
    private const string ZeroArgToolWithEmptyDeltaFixture = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_z1","model":"claude-opus-5-5","usage":{"input_tokens":42,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"PRIVATE_THINKING","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"PLAN"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"PRIVATE_SIGNATURE"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_zero","name":"list_catalog_layers","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":12}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string ZeroArgToolWithoutDeltaFixture = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_z2","model":"claude-opus-5-5","usage":{"input_tokens":15,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_nodelta","name":"list_catalog_layers","input":{}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":6}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string MultiToolCallFixture = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_m1","model":"claude-opus-5-5","usage":{"input_tokens":30,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"PRIVATE_THINKING","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"PRIVATE_SIGNATURE"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_a","name":"list_catalog_layers","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_b","name":"list_incidents","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"status\":\"open\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":18}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [UnitTest]
    public async Task StreamAsync_ZeroArgumentToolWithEmptyDelta_CompletesAndRoundTripsThinkingContinuation()
    {
        using var handler = new StudioAiProxySequenceHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ZeroArgToolWithEmptyDeltaFixture) },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TextTurnFixture) });
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        var initial = InitialContinuationRequest(identity);

        var first = await CollectAsync(adapter, initial);
        var toolStop = first.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop).Subject;
        toolStop.ToolCallId.Should().Be("toolu_zero");
        toolStop.ToolArguments!.Value.GetRawText().Should().Be("{}");
        first.Last().StopReason.Should().Be(StudioAiStopReason.ToolCall);

        var second = await CollectAsync(adapter, ZeroArgToolResultRequest(identity, initial));

        first.Concat(second).Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        handler.SendCount.Should().Be(2);
        using var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var assistant = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        assistant.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("thinking", "tool_use");
        assistant[0].GetProperty("signature").GetString().Should().Be("PRIVATE_SIGNATURE");
        assistant[1].GetProperty("input").GetRawText().Should().Be("{}");
    }

    [UnitTest]
    public async Task StreamAsync_ZeroArgumentToolWithoutDeltas_CompletesWithEmptyObjectArguments()
    {
        var events = await CollectAsync(CreateAdapter(ZeroArgToolWithoutDeltaFixture), ToolRequest());

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        var toolStop = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop).Subject;
        toolStop.ToolCallId.Should().Be("toolu_nodelta");
        toolStop.ToolArguments!.Value.GetRawText().Should().Be("{}");
        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
    }

    [UnitTheory]
    [InlineData("""{"type":"tool_use","id":"toolu_bad","name":"list_incidents"}""", "")]
    [InlineData("""{"type":"tool_use","id":"toolu_bad","name":"list_incidents","input":[]}""", "")]
    [InlineData("""{"type":"tool_use","id":"toolu_bad","name":"list_incidents","input":{}}""", "   ")]
    [InlineData("""{"type":"tool_use","id":"toolu_bad","name":"list_incidents","input":{}}""", "{\"status\":")]
    [InlineData("""{"type":"tool_use","id":"toolu_bad","name":"list_incidents","input":{}}""", "[1,2]")]
    public async Task StreamAsync_MalformedOrMissingToolArguments_FailsClosed(string blockJson, string partialJson)
    {
        var deltaFrame = partialJson.Length == 0
            ? string.Empty
            : $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonSerializer.Serialize(partialJson)}}}}}\n";
        var fixture = "data: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-5-5\"}}\n"
            + $"data: {{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{blockJson}}}\n"
            + deltaFrame
            + "data: {\"type\":\"content_block_stop\",\"index\":0}\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n"
            + "data: {\"type\":\"message_stop\"}\n";

        var events = await CollectAsync(CreateAdapter(fixture), ToolRequest());

        var failure = events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error).Subject;
        failure.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
    }

    [UnitTest]
    public async Task StreamAsync_ConsecutiveToolResults_MergesIntoSingleAnthropicUserMessage()
    {
        using var handler = new StudioAiProxySequenceHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MultiToolCallFixture) },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TextTurnFixture) });
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        var initial = InitialContinuationRequest(identity);

        var first = await CollectAsync(adapter, initial);
        first.Where(e => e.Type == StudioAiChatEventType.ToolCallStop).Should().HaveCount(2);

        var second = await CollectAsync(adapter, MultiToolResultRequest(identity, initial));

        first.Concat(second).Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        handler.SendCount.Should().Be(2);
        using var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);
        messages[2].GetProperty("role").GetString().Should().Be("user");
        var results = messages[2].GetProperty("content");
        results.GetArrayLength().Should().Be(2);
        results[0].GetProperty("tool_use_id").GetString().Should().Be("toolu_a");
        results[1].GetProperty("tool_use_id").GetString().Should().Be("toolu_b");
    }

    [UnitTest]
    public async Task StreamAsync_ToolCallArgumentsExceedingByteCap_FailsClosedWithError()
    {
        var chunk = new string('x', 70_000);
        var payload = $$$$"""
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_big","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":0}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_big","name":"list_catalog_layers","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"a\":\"{{{{chunk}}}}"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{{{{chunk}}}}"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{{{{chunk}}}}"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{{{{chunk}}}}\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":10}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        using var handler = new StudioAiProxySequenceHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var request = InitialContinuationRequest(AiExecutionContext.Create("test", "person-a", "studio-chat"));

        var events = await CollectAsync(adapter, request);

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error);
        var error = events.Single(e => e.Type == StudioAiChatEventType.Error);
        error.ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode);
        error.ErrorMessage.Should().Contain("maximum allowed size");
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.ToolCallStop);
    }

    private static StudioAiChatRequest ZeroArgToolResultRequest(
        AiExecutionContext identity,
        StudioAiChatRequest initial) => new()
        {
            ExecutionContext = identity,
            Tools = initial.Tools,
            Messages =
            [
                initial.Messages[0],
                new()
                {
                    Role = StudioAiRole.Assistant,
                    ToolCalls = [new() { Id = "toolu_zero", Name = "list_catalog_layers", Arguments = JsonElement.Parse("{}") }]
                },
                new() { Role = StudioAiRole.Tool, ToolCallId = "toolu_zero", Content = """["hi-parcels"]""" }
            ]
        };

    private static StudioAiChatRequest MultiToolResultRequest(
        AiExecutionContext identity,
        StudioAiChatRequest initial) => new()
        {
            ExecutionContext = identity,
            Tools = initial.Tools,
            Messages =
            [
                initial.Messages[0],
                new()
                {
                    Role = StudioAiRole.Assistant,
                    ToolCalls =
                    [
                        new() { Id = "toolu_a", Name = "list_catalog_layers", Arguments = JsonElement.Parse("{}") },
                        new() { Id = "toolu_b", Name = "list_incidents", Arguments = JsonElement.Parse("""{"status":"open"}""") }
                    ]
                },
                new() { Role = StudioAiRole.Tool, ToolCallId = "toolu_a", Content = """["hi-parcels"]""" },
                new() { Role = StudioAiRole.Tool, ToolCallId = "toolu_b", Content = "[]" }
            ]
        };
}
