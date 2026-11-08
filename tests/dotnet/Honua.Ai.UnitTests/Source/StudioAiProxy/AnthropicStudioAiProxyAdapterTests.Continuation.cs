// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Net;
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
    private const string RedactedThinkingFixture = """
        data: {"type":"content_block_start","index":8,"content_block":{"type":"redacted_thinking","data":"PRIVATE_REDACTED"}}
        data: {"type":"content_block_stop","index":8}

        """;

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_RedactedThinkingWithInvalidLifecycle_RejectsWithoutExposingContent(bool beforeStart)
    {
        var fixture = beforeStart ? RedactedThinkingFixture + TextTurnFixture
            : WithThinking(TextTurnFixture).Replace(ThinkingBlockFixture,
                RedactedThinkingFixture.Replace("data: {\"type\":\"content_block_stop\",\"index\":8}", "", StringComparison.Ordinal), StringComparison.Ordinal);

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop);
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_");
    }

    [UnitTheory]
    [InlineData("text")]
    [InlineData("thinking")]
    public async Task StreamAsync_PrivateCaptureLimitExceeded_CompletesVisibleText(string oversizedBlock)
    {
        var oversized = new string('x', AnthropicContinuationStore.MaximumTurnCharacters);
        var fixture = oversizedBlock == "text"
            ? TextTurnFixture.Replace("Hello", oversized, StringComparison.Ordinal)
            : WithThinking(TextTurnFixture).Replace("PRIVATE_THINKING", oversized, StringComparison.Ordinal);

        var events = await CollectAsync(CreateAdapter(fixture), ToolFreeRequest());

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        events.Last().StopReason.Should().Be(StudioAiStopReason.EndTurn);
        string.Concat(events.Where(e => e.Type == StudioAiChatEventType.TextDelta).Select(e => e.Text))
            .Should().Be(oversizedBlock == "text" ? oversized + " world" : "Hello world");
    }

    [UnitTest]
    public async Task StreamAsync_OversizedToolTurn_CompletesButRejectsContinuationBeforeHttp()
    {
        var fixture = WithThinking(ToolCallFixture).Replace("PRIVATE_THINKING",
            new string('x', AnthropicContinuationStore.MaximumTurnCharacters), StringComparison.Ordinal);
        var handler = new StudioAiProxyMockHttpMessageHandler(fixture);
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");

        var events = await CollectAsync(adapter, ContinuationRequest(identity, false));

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.ToolCallStop);
        events.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        events.Last().StopReason.Should().Be(StudioAiStopReason.ToolCall);
        JsonSerializer.Serialize(events).Should().NotContain("PRIVATE_");
        var continueTurn = () => CollectAsync(adapter, ContinuationRequest(identity, true));
        await continueTurn.Should().ThrowAsync<InvalidOperationException>();
        handler.SendCount.Should().Be(1);
    }

    [UnitTheory]
    [InlineData("credential")]
    [InlineData("provider")]
    public async Task StreamAsync_ContinuationCredentialOrProviderChanged_RejectsBeforeHttp(string changed)
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(WithThinking(ToolCallFixture));
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        await CollectAsync(adapter, ContinuationRequest(identity, false));
        var options = DefaultOptions();
        if (changed == "credential") options.ApiKey = "other-test-key";
        var prior = ContinuationRequest(identity, true);
        var request = new StudioAiChatRequest
        {
            Provider = changed == "provider" ? "other-provider" : prior.Provider,
            ExecutionContext = identity,
            Messages = prior.Messages,
            Tools = prior.Tools
        };

        var continueTurn = () => CollectAsync(adapter, options, request);

        await continueTurn.Should().ThrowAsync<InvalidOperationException>();
        handler.SendCount.Should().Be(1);
    }

    [UnitTest]
    public async Task StreamAsync_ThinkingToolRoundTrip_RestoresPrivateBlocksWithoutExposingThem()
    {
        var fixture = WithThinking(ToolCallFixture).Replace(ThinkingBlockFixture, ThinkingBlockFixture + RedactedThinkingFixture, StringComparison.Ordinal);
        var handler = new StudioAiProxySequenceHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture) },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TextTurnFixture) });
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");

        var first = await CollectAsync(adapter, ContinuationRequest(identity, false));
        var second = await CollectAsync(adapter, ContinuationRequest(identity, true));

        first.Concat(second).Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        handler.SendCount.Should().Be(2);
        second.Last().Type.Should().Be(StudioAiChatEventType.MessageStop);
        second.Last().StopReason.Should().Be(StudioAiStopReason.EndTurn);
        string.Concat(second.Where(e => e.Type == StudioAiChatEventType.TextDelta).Select(e => e.Text)).Should().Be("Hello world");
        using var body = JsonDocument.Parse(handler.CapturedRequestBody!);
        var assistant = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        assistant.EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should()
            .Equal("thinking", "redacted_thinking", "tool_use");
        assistant[0].GetProperty("thinking").GetString().Should().Be("PRIVATE_THINKINGPRIVATE_THINKING");
        assistant[0].GetProperty("signature").GetString().Should().Be("PRIVATE_SIGNATURE");
        assistant[1].GetProperty("data").GetString().Should().Be("PRIVATE_REDACTED");
        assistant[2].GetProperty("input").GetProperty("status").GetString().Should().Be("open");
        body.RootElement.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("type")
            .GetString().Should().Be("tool_result");
        JsonSerializer.Serialize(first.Concat(second)).Should().NotContain("PRIVATE_");
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_IncompleteOrPostTerminalTurn_DoesNotSaveContinuation(bool postTerminal)
    {
        var fixture = WithThinking(ToolCallFixture);
        fixture = postTerminal ? fixture + "\ndata: {\"type\":\"ping\"}\n" : fixture[..fixture.IndexOf("event: message_stop", StringComparison.Ordinal)];
        var handler = new StudioAiProxyMockHttpMessageHandler(fixture);
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        var events = await CollectAsync(adapter, ContinuationRequest(identity, false));
        events.Should().Contain(e => e.Type == StudioAiChatEventType.Error);

        var continueTurn = () => CollectAsync(adapter, ContinuationRequest(identity, true));

        await continueTurn.Should().ThrowAsync<InvalidOperationException>();
        handler.SendCount.Should().Be(1);
    }

    [UnitTheory]
    [InlineData("actor")]
    [InlineData("workflow")]
    [InlineData("deployment")]
    [InlineData("transcript")]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("system")]
    [InlineData("tools")]
    public async Task StreamAsync_ContinuationScopeChanged_DoesNotRestorePrivateBlocks(string changed)
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(WithThinking(ToolCallFixture));
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        await CollectAsync(adapter, ContinuationRequest(identity, false));
        var nextIdentity = changed switch
        {
            "actor" => AiExecutionContext.Create("test", "person-b", "studio-chat"),
            "workflow" => AiExecutionContext.Create("test", "person-a", "studio-chat", "other-session"),
            "deployment" => AiExecutionContext.Create("other", "person-a", "studio-chat"),
            _ => identity
        };
        var options = DefaultOptions();
        if (changed == "model") options.Model = "other-model";
        if (changed == "endpoint") options.Endpoint = "https://other.invalid";

        var continueTurn = () => CollectAsync(adapter, options,
            ContinuationRequest(nextIdentity, true, changed == "transcript" ? "different" : "list open incidents",
                changed == "system" ? "different system" : null, changed == "tools"));

        await continueTurn.Should().ThrowAsync<InvalidOperationException>();
        handler.SendCount.Should().Be(1);
        handler.CapturedRequestBody.Should().NotContain("PRIVATE_");
    }

    [UnitTest]
    public async Task StreamAsync_NewUserTurnWithChangedSystem_DoesNotRequireHistoricalContinuation()
    {
        var handler = new StudioAiProxyMockHttpMessageHandler(WithThinking(ToolCallFixture));
        using var clients = new StudioAiProxyMockHttpClientFactory(handler);
        var adapter = new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var identity = AiExecutionContext.Create("test", "person-a", "studio-chat");
        await CollectAsync(adapter, ContinuationRequest(identity, false));
        var prior = ContinuationRequest(identity, true);
        var next = new StudioAiChatRequest
        {
            ExecutionContext = identity,
            System = "Updated canvas context",
            Tools = prior.Tools,
            Messages = [.. prior.Messages, new() { Role = StudioAiRole.User, Content = "Next question" }]
        };

        var events = await CollectAsync(adapter, next);

        events.Should().NotContain(e => e.Type == StudioAiChatEventType.Error);
        handler.SendCount.Should().Be(2);
        handler.CapturedRequestBody.Should().NotContain("PRIVATE_");
    }

    private static StudioAiChatRequest ContinuationRequest(AiExecutionContext identity, bool includeResult,
        string prompt = "list open incidents", string? system = null, bool omitTools = false)
    {
        List<StudioAiMessage> messages = [new() { Role = StudioAiRole.User, Content = prompt }];
        if (includeResult)
        {
            messages.Add(new StudioAiMessage
            {
                Role = StudioAiRole.Assistant,
                ToolCalls = [new() { Id = "toolu_1", Name = "list_incidents", Arguments = JsonDocument.Parse("{\"status\":\"open\"}").RootElement.Clone() }]
            });
            messages.Add(new StudioAiMessage { Role = StudioAiRole.Tool, ToolCallId = "toolu_1", Content = "[]" });
        }
        return new StudioAiChatRequest
        {
            ExecutionContext = identity,
            Messages = messages,
            System = system,
            Tools = omitTools ? null : ToolRequest().Tools
        };
    }
}
