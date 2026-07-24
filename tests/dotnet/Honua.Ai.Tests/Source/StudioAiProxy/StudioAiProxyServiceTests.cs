// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// Orchestration tests for <see cref="StudioAiProxyService"/>: provider/default resolution, request
/// validation, capability descriptors, and — because <see cref="IStudioAiProxyAdapter"/> is a plugged-in
/// contract — the defense-in-depth backstop that guarantees exactly one terminal event (and a
/// correctly populated <see cref="StudioAiProxyCallSummary"/>) even when a fake adapter misbehaves.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyServiceTests
{
    [UnitTest]
    public async Task GetCapabilitiesAsync_WhenDisabled_ReturnsEnabledFalse()
    {
        var service = CreateService(new StudioAiProxyConfiguration { Enabled = false });

        var capabilities = await service.GetCapabilitiesAsync();

        capabilities.Enabled.Should().BeFalse();
        capabilities.Providers.Should().BeEmpty();
    }

    [UnitTest]
    public async Task GetCapabilitiesAsync_ReportsDefaultAndConfiguredFlags()
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        var service = CreateService(config, new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, isConfigured: true));

        var capabilities = await service.GetCapabilitiesAsync();

        capabilities.Enabled.Should().BeTrue();
        capabilities.DefaultProvider.Should().Be("claude");
        capabilities.Providers.Should().ContainSingle();
        capabilities.Providers[0].Provider.Should().Be("claude");
        capabilities.Providers[0].IsDefault.Should().BeTrue();
        capabilities.Providers[0].Configured.Should().BeTrue();
    }

    [UnitTest]
    public void ValidateRequest_NoMessages_IsRejected()
    {
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest { Messages = [] });

        error.Should().NotBeNull();
    }

    [UnitTest]
    public void ValidateRequest_UnknownProvider_IsRejected()
    {
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Provider = "does-not-exist",
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
        });

        error.Should().Contain("does-not-exist");
    }

    [UnitTest]
    public void ValidateRequest_ProviderMissingRequiredConfig_IsRejected()
    {
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, isConfigured: false));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
        });

        error.Should().NotBeNull();
    }

    [UnitTest]
    public void ValidateRequest_ToolsAgainstProviderThatDoesNotSupportThem_IsRejected()
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.Providers["claude"].SupportsTools = false;
        var service = CreateService(config, new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }],
            Tools = [new StudioAiToolDefinition { Name = "tool" }]
        });

        error.Should().NotBeNull();
    }

    [UnitTest]
    public void ValidateRequest_ExceedsMaxPromptCharacters_IsRejected()
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.MaxPromptCharacters = 5;
        var service = CreateService(config, new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "way too long for the limit" }]
        });

        error.Should().NotBeNull();
    }

    [UnitTest]
    public void ValidateRequest_WellFormedRequest_IsAccepted()
    {
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
        });

        error.Should().BeNull();
    }

    [UnitTest]
    public async Task StreamChatAsync_NormalCompletion_PopulatesSummaryFromMessageStop()
    {
        var adapter = new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true, request => Events(
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "claude-sonnet-4-5" },
            new StudioAiChatEvent { Type = StudioAiChatEventType.TextDelta, Text = "hi" },
            new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.MessageStop,
                StopReason = StudioAiStopReason.EndTurn,
                PromptTokens = 10,
                CompletionTokens = 3,
                LatencyMs = 42
            }));
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), adapter);

        var summary = new StudioAiProxyCallSummary();
        var request = new StudioAiChatRequest { Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }] };
        var events = await CollectAsync(service, request, summary);

        events.Should().HaveCount(3);
        summary.Succeeded.Should().BeTrue();
        summary.Provider.Should().Be("claude");
        summary.Kind.Should().Be(StudioAiProxyConfiguration.AnthropicKind);
        summary.PromptTokens.Should().Be(10);
        summary.CompletionTokens.Should().Be(3);
        summary.LatencyMs.Should().Be(42);
        summary.StopReason.Should().Be(StudioAiStopReason.EndTurn);
    }

    [UnitTest]
    public async Task StreamChatAsync_AdapterEmitsError_PopulatesSummaryAsFailure()
    {
        var adapter = new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true, request => Events(
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "claude-sonnet-4-5" },
            new StudioAiChatEvent { Type = StudioAiChatEventType.Error, ErrorMessage = "provider exploded", LatencyMs = 7 }));
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), adapter);

        var summary = new StudioAiProxyCallSummary();
        var request = new StudioAiChatRequest { Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }] };
        await CollectAsync(service, request, summary);

        summary.Succeeded.Should().BeFalse();
        summary.ErrorMessage.Should().Be("provider exploded");
        summary.StopReason.Should().Be(StudioAiStopReason.Error);
    }

    [UnitTest]
    public async Task StreamChatAsync_AdapterThrows_BackstopEmitsSingleErrorEvent()
    {
        var adapter = new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true, ThrowingEvents);
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), adapter);

        var summary = new StudioAiProxyCallSummary();
        var request = new StudioAiChatRequest { Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }] };
        var events = await CollectAsync(service, request, summary);

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error);
        summary.Succeeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task StreamChatAsync_AdapterEndsWithoutTerminalEvent_BackstopAppendsErrorEvent()
    {
        var adapter = new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true, request => Events(
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "claude-sonnet-4-5" }));
        var service = CreateService(ConfigWithOneAnthropicProvider("claude", isDefault: true), adapter);

        var summary = new StudioAiProxyCallSummary();
        var request = new StudioAiChatRequest { Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }] };
        var events = await CollectAsync(service, request, summary);

        events.Last().Type.Should().Be(StudioAiChatEventType.Error);
        summary.Succeeded.Should().BeFalse();
    }

    private static async IAsyncEnumerable<StudioAiChatEvent> ThrowingEvents(StudioAiChatRequest request)
    {
        await Task.Yield();
        throw new InvalidOperationException("adapter misbehaved");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<StudioAiChatEvent> Events(params StudioAiChatEvent[] events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    private static async Task<List<StudioAiChatEvent>> CollectAsync(
        StudioAiProxyService service,
        StudioAiChatRequest request,
        StudioAiProxyCallSummary summary)
    {
        List<StudioAiChatEvent> events = [];
        await foreach (var evt in service.StreamChatAsync(request, summary, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    private static StudioAiProxyService CreateService(StudioAiProxyConfiguration configuration, params IStudioAiProxyAdapter[] adapters)
        => new(Options.Create(configuration), adapters, NullLogger<StudioAiProxyService>.Instance);

    private static StudioAiProxyConfiguration ConfigWithOneAnthropicProvider(string name, bool isDefault) => new()
    {
        Enabled = true,
        DefaultProvider = isDefault ? name : string.Empty,
        Providers =
        {
            [name] = new StudioAiProxyProviderOptions
            {
                Kind = StudioAiProxyConfiguration.AnthropicKind,
                Endpoint = "https://api.anthropic.com",
                Model = "claude-sonnet-4-5",
                ApiKey = "test-key",
                MaxTokens = 4096,
                TimeoutSeconds = 60
            }
        }
    };

    private sealed class FakeAdapter : IStudioAiProxyAdapter
    {
        private readonly bool _isConfigured;
        private readonly Func<StudioAiChatRequest, IAsyncEnumerable<StudioAiChatEvent>>? _stream;

        public FakeAdapter(string kind, bool isConfigured, Func<StudioAiChatRequest, IAsyncEnumerable<StudioAiChatEvent>>? stream = null)
        {
            Kind = kind;
            _isConfigured = isConfigured;
            _stream = stream;
        }

        public string Kind { get; }

        public bool IsConfigured(StudioAiProxyProviderOptions options) => _isConfigured;

        public IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
            StudioAiProxyProviderOptions options, StudioAiChatRequest request, CancellationToken cancellationToken)
            => _stream is null ? Events() : _stream(request);
    }
}
