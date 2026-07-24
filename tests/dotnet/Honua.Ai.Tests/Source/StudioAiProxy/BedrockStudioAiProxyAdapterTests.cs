// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Contract tests for <see cref="BedrockStudioAiProxyAdapter"/> against a fake
/// <see cref="IChatClient"/> standing in for <c>BedrockChatClientAdapter</c> — no AWS account, no
/// live Bedrock call. Proves the bridge: <see cref="ChatResponseUpdate"/> content
/// (<see cref="TextContent"/> / <see cref="FunctionCallContent"/> / <see cref="UsageContent"/>) maps
/// onto the same neutral <see cref="StudioAiChatEvent"/> sequence the HTTP-based adapters produce.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class BedrockStudioAiProxyAdapterTests
{
    [UnitTest]
    public async Task StreamAsync_TextTurn_EmitsDeltasThenMessageStopWithUsage()
    {
        var updates = new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, "Hello"),
            new(ChatRole.Assistant, " world"),
            new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5 })]),
            new() { FinishReason = ChatFinishReason.Stop }
        };

        var adapter = CreateAdapter(updates);
        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Select(e => e.Type).Should().Equal(
            StudioAiChatEventType.MessageStart,
            StudioAiChatEventType.TextDelta,
            StudioAiChatEventType.TextDelta,
            StudioAiChatEventType.MessageStop);

        var stop = events.Last();
        stop.StopReason.Should().Be(StudioAiStopReason.EndTurn);
        stop.PromptTokens.Should().Be(12);
        stop.CompletionTokens.Should().Be(5);
    }

    [UnitTest]
    public async Task StreamAsync_ToolCallTurn_EmitsStartDeltaStopWithFullArguments()
    {
        var call = new FunctionCallContent("call-1", "list_incidents", new Dictionary<string, object?> { ["status"] = "open" });
        var updates = new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, [call]),
            new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 20, OutputTokenCount = 8 })]),
            new() { FinishReason = ChatFinishReason.ToolCalls }
        };

        var adapter = CreateAdapter(updates);
        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Select(e => e.Type).Should().Equal(
            StudioAiChatEventType.MessageStart,
            StudioAiChatEventType.ToolCallStart,
            StudioAiChatEventType.ToolCallDelta,
            StudioAiChatEventType.ToolCallStop,
            StudioAiChatEventType.MessageStop);

        events[1].ToolCallId.Should().Be("call-1");
        events[1].ToolName.Should().Be("list_incidents");

        var stop = events[3];
        stop.ToolArguments.Should().NotBeNull();
        stop.ToolArguments!.Value.GetProperty("status").GetString().Should().Be("open");

        events.Last().StopReason.Should().Be(StudioAiStopReason.ToolCall);
    }

    [UnitTest]
    public async Task StreamAsync_ClientThrows_EmitsSingleErrorEvent()
    {
        var adapter = new BedrockStudioAiProxyAdapter(
            new FakeBedrockChatClientFactory(new ThrowingChatClient()),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<BedrockStudioAiProxyAdapter>.Instance);

        var events = await CollectAsync(adapter, ToolFreeRequest());

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error);
    }

    [UnitTest]
    public void IsConfigured_RequiresOnlyModel()
    {
        var adapter = new BedrockStudioAiProxyAdapter(
            new FakeBedrockChatClientFactory(new FakeStreamingChatClient([])),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<BedrockStudioAiProxyAdapter>.Instance);

        adapter.Kind.Should().Be(StudioAiProxyConfiguration.BedrockKind);
        adapter.IsConfigured("bedrock", new StudioAiProxyProviderOptions { Model = "us.anthropic.claude-sonnet-4-5", Region = "us-west-2" }).Should().BeTrue();
        adapter.IsConfigured("bedrock", new StudioAiProxyProviderOptions { Region = "us-west-2" }).Should().BeFalse();
    }

    private static BedrockStudioAiProxyAdapter CreateAdapter(List<ChatResponseUpdate> updates)
        => new(
            new FakeBedrockChatClientFactory(new FakeStreamingChatClient(updates)),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<BedrockStudioAiProxyAdapter>.Instance);

    private static async Task<List<StudioAiChatEvent>> CollectAsync(BedrockStudioAiProxyAdapter adapter, StudioAiChatRequest request)
    {
        var options = new StudioAiProxyProviderOptions
        {
            Kind = StudioAiProxyConfiguration.BedrockKind,
            Model = "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
            Region = "us-west-2",
            MaxTokens = 1024,
            TimeoutSeconds = 30
        };

        List<StudioAiChatEvent> events = [];
        await foreach (var evt in adapter.StreamAsync(options, request, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    private static StudioAiChatRequest ToolFreeRequest() => new()
    {
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
    };

    private sealed class FakeBedrockChatClientFactory(IChatClient client) : IBedrockChatClientFactory
    {
        public IChatClient Create(string model, string region, string? apiKey) => client;
    }

    /// <summary>Yields a fixed list of updates — stands in for the Converse-backed streaming adapter.</summary>
    private sealed class FakeStreamingChatClient(IReadOnlyList<ChatResponseUpdate> updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming-only fake.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Simulates an AWS SDK transport/auth failure surfacing mid-stream.</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming-only fake.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("simulated Bedrock transport failure");
#pragma warning disable CS0162 // Unreachable code: required so the compiler treats this as an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
