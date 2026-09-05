// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Adapters.Bedrock;
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
    public async Task StreamAsync_ToolContractMetadata_IsIncludedInProviderDescription()
    {
        var client = new CapturingStreamingChatClient();
        var adapter = new BedrockStudioAiProxyAdapter(
            new FakeBedrockChatClientFactory(client),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<BedrockStudioAiProxyAdapter>.Instance);
        var request = new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }],
            Tools =
            [
                new StudioAiToolDefinition
                {
                    Name = "lookup",
                    InputSchema = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone(),
                    Annotations = System.Text.Json.JsonDocument.Parse("{\"readOnlyHint\":true}").RootElement.Clone(),
                    OutputSchema = System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                }
            ]
        };

        await CollectAsync(adapter, request);

        client.Options!.Tools.Should().ContainSingle();
        client.Options.Tools[0].Should().BeAssignableTo<AIFunction>()
            .Which.Description.Should().Contain("Tool annotations (JSON): {\"readOnlyHint\":true}")
            .And.Contain("Expected structured output schema (JSON): {\"type\":\"object\"}");
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
    public async Task StreamAsync_Timeout_EmitsErrorAndDisposesEnumerator()
    {
        var stalled = new StallingChatClient();
        var healthy = new FakeStreamingChatClient(
        [
            new(ChatRole.Assistant, "recovered"),
            new() { FinishReason = ChatFinishReason.Stop }
        ]);
        var factory = new SequenceBedrockChatClientFactory([stalled, healthy]);
        var adapter = new BedrockStudioAiProxyAdapter(
            factory,
            new StudioAiProxyApiKeyResolver(),
            NullLogger<BedrockStudioAiProxyAdapter>.Instance);
        var options = DefaultOptions();
        options.TimeoutSeconds = 1;

        var stopwatch = Stopwatch.StartNew();
        var events = await CollectAsync(adapter, options, ToolFreeRequest());
        stopwatch.Stop();

        // The stall is bounded by the configured provider deadline, not by the stream.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        events.Select(e => e.Type).Should().Equal(
            StudioAiChatEventType.MessageStart,
            StudioAiChatEventType.Error);
        events[^1].ErrorMessage.Should().Be("Provider request timed out.");

        // The upstream enumerator and the per-request chat client are both released.
        stalled.EnumeratorsDisposed.Should().Be(1);
        stalled.WasDisposed.Should().BeTrue();

        // Recovery: the next request gets a fresh client and reaches a successful terminal.
        var recovered = await CollectAsync(adapter, options, ToolFreeRequest());

        factory.CreateCount.Should().Be(2);
        recovered.Select(e => e.Type).Should().Equal(
            StudioAiChatEventType.MessageStart,
            StudioAiChatEventType.TextDelta,
            StudioAiChatEventType.MessageStop);
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

    private static StudioAiProxyProviderOptions DefaultOptions() => new()
    {
        Kind = StudioAiProxyConfiguration.BedrockKind,
        Model = "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
        Region = "us-west-2",
        MaxTokens = 1024,
        TimeoutSeconds = 30
    };

    private static Task<List<StudioAiChatEvent>> CollectAsync(BedrockStudioAiProxyAdapter adapter, StudioAiChatRequest request)
        => CollectAsync(adapter, DefaultOptions(), request);

    private static async Task<List<StudioAiChatEvent>> CollectAsync(
        BedrockStudioAiProxyAdapter adapter,
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

    private static StudioAiChatRequest ToolFreeRequest() => new()
    {
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
    };

    private sealed class FakeBedrockChatClientFactory(IChatClient client) : IBedrockChatClientFactory
    {
        public IChatClient Create(string model, string region, string? apiKey) => client;
    }

    /// <summary>
    /// Hands out a different <see cref="IChatClient"/> per request, so a test can prove the adapter
    /// builds a fresh client for every stream rather than reusing a poisoned one.
    /// </summary>
    private sealed class SequenceBedrockChatClientFactory(IReadOnlyList<IChatClient> clients) : IBedrockChatClientFactory
    {
        public int CreateCount { get; private set; }

        public IChatClient Create(string model, string region, string? apiKey)
            => clients[Math.Min(CreateCount++, clients.Count - 1)];
    }

    /// <summary>
    /// Never produces an update: the stream stalls until the adapter's deadline cancels it. Records
    /// enumerator disposal and client disposal so the outage matrix's Bedrock
    /// <c>stall-before-first-event</c> case can assert the upstream is released.
    /// </summary>
    private sealed class StallingChatClient : IChatClient
    {
        private int _enumeratorsDisposed;

        public int EnumeratorsDisposed => Volatile.Read(ref _enumeratorsDisposed);

        public bool WasDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming-only fake.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => new StallingStream(this, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => WasDisposed = true;

        private sealed class StallingStream(StallingChatClient owner, CancellationToken streamToken)
            : IAsyncEnumerable<ChatResponseUpdate>
        {
            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
                => new StallingEnumerator(owner, streamToken.CanBeCanceled ? streamToken : cancellationToken);
        }

        private sealed class StallingEnumerator(StallingChatClient owner, CancellationToken cancellationToken)
            : IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current
                => throw new InvalidOperationException("The stalled stream never produces an update.");

            public async ValueTask<bool> MoveNextAsync()
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return false;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._enumeratorsDisposed);
                return ValueTask.CompletedTask;
            }
        }
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

    private sealed class CapturingStreamingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming-only fake.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            await Task.Yield();
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
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
