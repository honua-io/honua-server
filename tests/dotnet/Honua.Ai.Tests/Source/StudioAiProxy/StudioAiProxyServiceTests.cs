// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FluentAssertions;
using System.Text.Json;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Adapters;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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
    public async Task GetCapabilitiesAsync_AnthropicProviderWithoutApiKeyOrEnvFallback_ReportsUnconfigured()
    {
        // honua-server#3010 review: exercises the REAL AnthropicStudioAiProxyAdapter (not the fake)
        // so this proves the service + adapter together, not just a mocked contract.
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.Providers["claude"].ApiKey = string.Empty;
        var realAdapter = new AnthropicStudioAiProxyAdapter(
            Substitute.For<IHttpClientFactory>(),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var service = CreateService(config, realAdapter);

        var capabilities = await service.GetCapabilitiesAsync();

        capabilities.Providers.Should().ContainSingle();
        capabilities.Providers[0].Configured.Should().BeFalse(
            "a provider with no ApiKey and no per-provider environment fallback can never be called");
    }

    [UnitTest]
    public void ValidateRequest_AnthropicProviderWithoutApiKeyOrEnvFallback_IsRejectedBeforeStreamingStarts()
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.Providers["claude"].ApiKey = string.Empty;
        var realAdapter = new AnthropicStudioAiProxyAdapter(
            Substitute.For<IHttpClientFactory>(),
            new StudioAiProxyApiKeyResolver(),
            NullLogger<AnthropicStudioAiProxyAdapter>.Instance);
        var service = CreateService(config, realAdapter);

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
        });

        error.Should().NotBeNull(
            "the endpoint must return a 400 before committing an SSE stream, not stream an immediate error event");
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
    public void ValidateRequest_ToolPayloadCountsTowardMaxPromptCharacters()
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.MaxPromptCharacters = 20;
        var service = CreateService(config, new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }],
            Tools =
            [
                new StudioAiToolDefinition
                {
                    Name = "lookup",
                    Description = "tool description that exceeds the request budget",
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                }
            ]
        });

        error.Should().Contain("configured limit");
    }

    [UnitTest]
    public void ValidateRequest_ToolContractMetadataCountsTowardMaxPromptCharacters()
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.MaxPromptCharacters = 30;
        var service = CreateService(config, new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }],
            Tools =
            [
                new StudioAiToolDefinition
                {
                    Name = "lookup",
                    InputSchema = JsonDocument.Parse("{}").RootElement.Clone(),
                    Annotations = JsonDocument.Parse("{\"readOnlyHint\":true}").RootElement.Clone(),
                    OutputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                }
            ]
        });

        error.Should().Contain("configured limit");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateRequest_OversizedToolContractComponent_IsRejected(bool useAnnotations)
    {
        var service = CreateService(
            ConfigWithOneAnthropicProvider("claude", isDefault: true),
            new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));
        using var oversized = JsonDocument.Parse($"{{\"value\":\"{new string('x', 64_001)}\"}}");

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }],
            Tools =
            [
                new StudioAiToolDefinition
                {
                    Name = "lookup",
                    InputSchema = JsonDocument.Parse("{}").RootElement.Clone(),
                    Annotations = useAnnotations ? oversized.RootElement.Clone() : null,
                    OutputSchema = useAnnotations ? null : oversized.RootElement.Clone()
                }
            ]
        });

        error.Should().Contain(useAnnotations ? "annotations" : "output schema");
    }

    [UnitTest]
    public void ValidateRequest_TooManyTools_IsRejected()
    {
        var service = CreateService(
            ConfigWithOneAnthropicProvider("claude", isDefault: true),
            new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true));

        var error = service.ValidateRequest(new StudioAiChatRequest
        {
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }],
            Tools = Enumerable.Range(0, 129)
                .Select(index => new StudioAiToolDefinition
                {
                    Name = $"tool-{index}",
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                })
                .ToArray()
        });

        error.Should().Contain("maximum of 128 tools");
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

    [UnitTest]
    public Task StreamChatAsync_DeployTargetBindingFails_AppendsTypedErrorAndCorrectsSummary()
        => AssertGovernedTargetBindingFailsAsync("honua_propose_deploy_operation");

    [UnitTest]
    public Task StreamChatAsync_DeployPlanTargetBindingFails_AppendsTypedErrorAndCorrectsSummary()
        => AssertGovernedTargetBindingFailsAsync("honua_propose_deploy_plan");

    [UnitTest]
    public Task StreamChatAsync_RollbackTargetBindingFails_AppendsTypedErrorAndCorrectsSummary()
        => AssertGovernedTargetBindingFailsAsync("honua_propose_rollback");

    [UnitTest]
    public Task StreamChatAsync_FindingCandidateBindingFails_AppendsTypedErrorAndCorrectsSummary()
        => AssertGovernedTargetBindingFailsAsync(
            "honua_propose_finding",
            """{"findingId":"runtime-divergence","candidateId":"candidate-other"}""");

    private static async Task AssertGovernedTargetBindingFailsAsync(
        string toolName,
        string argumentJson = """{"targetId":"candidate-other"}""")
    {
        var config = ConfigWithOneAnthropicProvider("claude", isDefault: true);
        config.TranscriptSigning.KeyId = "test-key";
        config.TranscriptSigning.PrivateKeyReference = "secret://studio-key";
        var secrets = Substitute.For<ISecretProvider>();
        secrets.IsSecretReference("secret://studio-key").Returns(true);
        secrets.GetSecretOrDefaultAsync("secret://studio-key", null, Arg.Any<CancellationToken>())
            .Returns(Convert.ToBase64String(new byte[32]));
        using var arguments = JsonDocument.Parse(argumentJson);
        var adapter = new FakeAdapter(StudioAiProxyConfiguration.AnthropicKind, true, request => Events(
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "claude-sonnet-4-5" },
            new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStart, ToolCallId = "call-1", ToolName = toolName },
            new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStop, ToolCallId = "call-1", ToolArguments = arguments.RootElement.Clone() },
            new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStop, StopReason = StudioAiStopReason.ToolCall }));
        var service = new StudioAiProxyService(
            Options.Create(config),
            [adapter],
            new StudioAiTranscriptSigner(Options.Create(config), TimeProvider.System, secrets),
            NullLogger<StudioAiProxyService>.Instance);
        var summary = new StudioAiProxyCallSummary();
        var request = new StudioAiChatRequest
        {
            Certification = new StudioAiTranscriptCertification
            {
                CandidateId = "candidate-7",
                ReleaseId = "release-9",
                EndpointIdentity = "candidate-proxy",
                ActionId = "deploy",
                RunNonce = "nonce"
            },
            Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "deploy" }]
        };

        var events = await CollectAsync(service, request, summary);

        events.Last().Should().Match<StudioAiChatEvent>(evt =>
            evt.Type == StudioAiChatEventType.Error
            && evt.ErrorCode == "studio_ai/provenance_validation_failed");
        events.Should().NotContain(evt => evt.Type == StudioAiChatEventType.MessageStop);
        summary.Succeeded.Should().BeFalse();
        summary.StopReason.Should().Be(StudioAiStopReason.Error);
        summary.ErrorMessage.Should().Be("Transcript provenance validation failed.");
    }

    public static IEnumerable<object[]> InvalidStreamFixtures()
    {
        var start = new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStart, Model = "provider-model" };
        var stop = new StudioAiChatEvent { Type = StudioAiChatEventType.MessageStop, StopReason = StudioAiStopReason.EndTurn };
        var toolStart = new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStart, ToolCallId = "call-1", ToolName = "lookup" };
        var toolDelta = new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallDelta, ToolCallId = "call-1", ToolArgumentsDelta = "{" };
        var toolStop = new StudioAiChatEvent { Type = StudioAiChatEventType.ToolCallStop, ToolCallId = "call-1", ToolArguments = JsonDocument.Parse("{}").RootElement.Clone() };
        var fixtures = new (string Id, StudioAiChatEvent[] Events)[]
        {
            ("missing-message-start", [new() { Type = StudioAiChatEventType.TextDelta, Text = "x" }, stop]),
            ("missing-provider-model", [new() { Type = StudioAiChatEventType.MessageStart }, stop]),
            ("tool-delta-without-start", [start, new() { Type = StudioAiChatEventType.ToolCallDelta, ToolCallId = "call-1", ToolArgumentsDelta = "{}" }, stop]),
            ("duplicate-tool-id", [start, toolStart, toolStop, toolStart, toolStop, stop]),
            ("tool-stop-without-start", [start, toolStop, stop]),
            ("invalid-tool-json", [start, toolStart, new() { Type = StudioAiChatEventType.ToolCallStop, ToolCallId = "call-1" }, stop]),
            ("truncated-tool-json", [start, toolStart, toolDelta, stop]),
            ("message-stop-before-tool-stop", [start, toolStart, stop]),
            ("duplicate-message-stop", [start, stop, stop]),
            ("error-after-message-stop", [start, stop, new() { Type = StudioAiChatEventType.Error, ErrorMessage = "late" }]),
            ("text-after-message-stop", [start, stop, new() { Type = StudioAiChatEventType.TextDelta, Text = "late" }]),
            ("tool-after-message-stop", [start, stop, toolStart]),
            ("adapter-provenance", [start, new() { Type = StudioAiChatEventType.TranscriptProvenance }, stop])
        };

        foreach (var kind in new[] { StudioAiProxyConfiguration.OpenAiKind, StudioAiProxyConfiguration.AnthropicKind, StudioAiProxyConfiguration.BedrockKind })
        {
            foreach (var fixture in fixtures)
            {
                yield return [kind, fixture.Id, fixture.Events];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidStreamFixtures))]
    public async Task StreamChatAsync_InvalidProviderGrammar_RejectsBeforeSuccessfulTerminal(
        string providerKind,
        string fixtureId,
        StudioAiChatEvent[] providerEvents)
    {
        var config = ConfigWithProvider(providerKind);
        var adapter = new TrackingAdapter(providerKind, providerEvents);
        var service = CreateService(config, adapter);
        var summary = new StudioAiProxyCallSummary();

        var events = await CollectAsync(service, ValidRequest(), summary);

        events.Should().ContainSingle(e => e.Type == StudioAiChatEventType.Error, fixtureId);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.MessageStop, fixtureId);
        events.Should().NotContain(e => e.Type == StudioAiChatEventType.TranscriptProvenance, fixtureId);
        events.Last().ErrorCode.Should().Be(StudioAiStreamGrammarValidator.InvalidStreamCode, fixtureId);
        summary.Succeeded.Should().BeFalse(fixtureId);
        adapter.DisposeCount.Should().Be(1, fixtureId);
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
        => new(
            Options.Create(configuration),
            adapters,
            new StudioAiTranscriptSigner(Options.Create(configuration), TimeProvider.System),
            NullLogger<StudioAiProxyService>.Instance);

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

    private static StudioAiProxyConfiguration ConfigWithProvider(string kind) => new()
    {
        Enabled = true,
        DefaultProvider = "provider",
        Providers =
        {
            ["provider"] = new StudioAiProxyProviderOptions
            {
                Kind = kind,
                Endpoint = "https://provider.example",
                Model = "provider-model",
                ApiKey = "test-key",
                MaxTokens = 4096,
                TimeoutSeconds = 60
            }
        }
    };

    private static StudioAiChatRequest ValidRequest() => new()
    {
        Messages = [new StudioAiMessage { Role = StudioAiRole.User, Content = "hi" }]
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

        public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options) => _isConfigured;

        public IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
            StudioAiProxyProviderOptions options, StudioAiChatRequest request, CancellationToken cancellationToken)
            => _stream is null ? Events() : _stream(request);
    }

    private sealed class TrackingAdapter(string kind, StudioAiChatEvent[] events) : IStudioAiProxyAdapter
    {
        public string Kind { get; } = kind;

        public int DisposeCount { get; private set; }

        public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options) => true;

        public IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
            StudioAiProxyProviderOptions options,
            StudioAiChatRequest request,
            CancellationToken cancellationToken) => Stream(cancellationToken);

        private async IAsyncEnumerable<StudioAiChatEvent> Stream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                foreach (var evt in events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return evt;
                }
            }
            finally
            {
                DisposeCount++;
            }
        }
    }
}
