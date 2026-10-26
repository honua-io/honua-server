// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters.Bedrock;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Ai.StudioAiProxy;
using Honua.Server.Tests.Features.StudioAiProxy.Fakes;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Provider failure diagnostics must never retain response text or exception payloads.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyLoggingPrivacyTests
{
    private const string Canary = "PRIVATE_PROMPT_API_KEY_CANARY";

    [UnitTheory]
    [InlineData(false, "status", 6752)]
    [InlineData(true, "status", 6752)]
    [InlineData(false, "send", 6754)]
    [InlineData(true, "send", 6754)]
    [InlineData(false, "parse", 6755)]
    [InlineData(true, "parse", 6755)]
    public async Task StreamAsync_HttpFailure_LogsOnlyControlledDiagnostics(bool anthropic, string failure, int eventId)
    {
        using var logs = new CapturingLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        using var clients = new StudioAiProxyMockHttpClientFactory(new FailureHandler(failure));
        IStudioAiProxyAdapter adapter = anthropic
            ? new AnthropicStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(), factory.CreateLogger<AnthropicStudioAiProxyAdapter>())
            : new OpenAiCompatibleStudioAiProxyAdapter(clients, new StudioAiProxyApiKeyResolver(), factory.CreateLogger<OpenAiCompatibleStudioAiProxyAdapter>());

        var events = await CollectAsync(adapter);

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error);
        events.Single(e => e.Type == StudioAiChatEventType.Error).ErrorMessage.Should().Be(failure switch
        {
            "status" => "Provider returned HTTP 503.",
            "send" => "Provider request failed.",
            _ => "Provider returned a malformed stream frame."
        });
        events.Should().OnlyContain(e => e.ErrorMessage == null || !e.ErrorMessage.Contains(Canary));
        AssertPrivate(logs, eventId);
        if (failure == "status")
        {
            logs.Entries.Single(e => e.Id == eventId).State.Should().Contain("503");
        }
        else
        {
            logs.Entries.Single(e => e.Id == eventId).State.Should().Contain(failure == "send" ? "HttpRequestException" : "JsonException");
        }
    }

    [UnitTest]
    public async Task StreamAsync_BedrockException_LogsTypeWithoutExceptionPayload()
    {
        using var logs = new CapturingLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var adapter = new BedrockStudioAiProxyAdapter(new ThrowingBedrockFactory(), new StudioAiProxyApiKeyResolver(), factory.CreateLogger<BedrockStudioAiProxyAdapter>());

        var events = await CollectAsync(adapter);

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error);
        AssertPrivate(logs, 6754);
        logs.Entries.Single(e => e.Id == 6754).State.Should().Contain("InvalidOperationException");
    }

    private static void AssertPrivate(CapturingLogs logs, int eventId)
    {
        logs.Entries.Should().ContainSingle(e => e.Id == eventId);
        logs.Entries.Should().OnlyContain(e => e.Exception == null);
        foreach (var entry in logs.Entries)
        {
            entry.Message.Should().NotContain(Canary);
            entry.State.Should().NotContain(Canary);
        }
    }

    private static async Task<List<StudioAiChatEvent>> CollectAsync(IStudioAiProxyAdapter adapter)
    {
        var options = new StudioAiProxyProviderOptions
        {
            Endpoint = "https://provider.example/v1",
            Model = "model",
            ApiKey = "test-key",
            ApiKeyFromEnvironment = true,
            Region = "us-west-2"
        };
        var request = new StudioAiChatRequest { Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = Canary }] };
        var events = new List<StudioAiChatEvent>();
        await foreach (var item in adapter.StreamAsync(options, request, RecordingAiAdmission.TransportPermit(), CancellationToken.None))
        {
            events.Add(item);
        }
        return events;
    }

    private sealed class FailureHandler(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure == "send")
            {
                throw new HttpRequestException(Canary, new IOException(Canary));
            }
            return Task.FromResult(new HttpResponseMessage(failure == "status" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(failure == "status" ? Canary : $"event: content_block_delta\ndata: {{\"{Canary}\": invalid}}\n\n")
            });
        }
    }

    private sealed class ThrowingBedrockFactory : IBedrockChatClientFactory
    {
        public IChatClient Create(string model, string region, string? apiKey) => new ThrowingChatClient();
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException(Canary, new IOException(Canary));
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record Entry(int Id, string Message, string State, Exception? Exception);
    private sealed class CapturingLogs : ILoggerProvider
    {
        public List<Entry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Entries);
        public void Dispose() { }
        private sealed class CaptureLogger(List<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var structured = state is IEnumerable<KeyValuePair<string, object?>> values
                    ? string.Join(";", values.Select(pair => $"{pair.Key}={pair.Value}")) : state?.ToString() ?? "";
                entries.Add(new Entry(eventId.Id, formatter(state, exception), structured, exception));
            }
        }
    }
}
