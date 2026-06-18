// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.DashboardGeneration;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.ReportGeneration;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.Publishing.Dashboards;
using Honua.Core.Features.Publishing.Reports;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Providers.Bedrock;

/// <summary>
/// Offline wiring tests for the AWS Bedrock (Claude) provider that backs the studio generation
/// flows. These prove — without an AWS account — that:
/// <list type="bullet">
///   <item>The shared config accepts the <c>bedrock</c> provider id (no endpoint/HTTPS required).</item>
///   <item>The dashboard/report services route to the Converse-API backend when the selected
///   provider is <c>bedrock</c>, mapping a forced tool-call into the proposal contract.</item>
///   <item>The Bedrock workflow provider self-gates on a configured model id.</item>
/// </list>
/// A live end-to-end test against real Bedrock lives in <c>BedrockStudioLiveTests</c>, gated on
/// <c>HONUA_AI_LIVE_BEDROCK=1</c>.
/// </summary>
public sealed class BedrockStudioProviderTests
{
    [UnitTest]
    public void ConfigurationValidator_AcceptsBedrockProviderWithoutEndpointOrKey()
    {
        var validator = new WorkflowGenerationConfigurationValidator();
        var options = new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.BedrockProviderId,
            Providers =
            {
                [WorkflowGenerationConfiguration.BedrockProviderId] = new WorkflowGenerationProviderOptions
                {
                    // No endpoint, no API key — Bedrock uses region + the AWS credential chain.
                    Model = "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
                    Region = "us-west-2"
                }
            }
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue(because: "Bedrock needs only a model id, not an endpoint or key");
    }

    [UnitTest]
    public void ConfigurationValidator_RejectsBedrockProviderWithoutModel()
    {
        var validator = new WorkflowGenerationConfigurationValidator();
        var options = new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.BedrockProviderId,
            Providers =
            {
                [WorkflowGenerationConfiguration.BedrockProviderId] = new WorkflowGenerationProviderOptions
                {
                    Region = "us-west-2"
                }
            }
        };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
    }

    [UnitTest]
    public async Task DashboardService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        // A canned clarification proposal: avoids constructing a full valid dashboard while still
        // proving the tool-call -> proposal mapping and that the Bedrock path was taken.
        const string ToolJson = """
        {
          "status": "needs-clarification",
          "rationale": "Which fleet region should the dashboard scope to?",
          "clarifications": [
            { "id": "region", "kind": "choice", "prompt": "Pick a region", "choices": [ { "id": "us", "label": "US" } ] }
          ]
        }
        """;

        var factory = FakeFactoryReturning(ToolJson, out var capturedModel, out var capturedRegion);
        var service = new DashboardGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<DashboardGenerationService>.Instance);

        var result = await service.GenerateAsync(new DashboardGenerationRequest { Prompt = "fleet overview" });

        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Model.Should().Be("test-bedrock-model");
        result.Clarifications.Should().ContainSingle(c => c.Id == "region");
        capturedModel.Value.Should().Be("test-bedrock-model");
        capturedRegion.Value.Should().Be("us-west-2");
    }

    [UnitTest]
    public async Task ReportService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        const string ToolJson = """
        { "status": "needs-clarification", "rationale": "Which quarter?", "clarifications": [] }
        """;

        var factory = FakeFactoryReturning(ToolJson, out _, out _);
        var service = new ReportGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<ReportGenerationService>.Instance);

        var result = await service.GenerateAsync(new ReportGenerationRequest { Prompt = "quarterly summary" });

        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
    }

    [UnitTest]
    public async Task DashboardService_WhenBedrockReturnsNoToolCall_ReturnsAnError()
    {
        var factory = Substitute.For<IBedrockChatClientFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "no tool call"))));

        var service = new DashboardGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<DashboardGenerationService>.Instance);

        var result = await service.GenerateAsync(new DashboardGenerationRequest { Prompt = "fleet overview" });

        result.Status.Should().Be("error");
    }

    [UnitTest]
    public void BedrockWorkflowProvider_IsConfigured_WhenModelIsSet()
    {
        var configured = new BedrockWorkflowGenerationProvider(
            WorkflowGenerationConfiguration.BedrockProviderId,
            Substitute.For<IBedrockChatClientFactory>(),
            BedrockOptions(),
            NullLogger<BedrockWorkflowGenerationProvider>.Instance);

        configured.IsConfigured.Should().BeTrue();
        configured.ProviderId.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);

        var unconfigured = new BedrockWorkflowGenerationProvider(
            WorkflowGenerationConfiguration.BedrockProviderId,
            Substitute.For<IBedrockChatClientFactory>(),
            Options.Create(new WorkflowGenerationConfiguration { Enabled = true }),
            NullLogger<BedrockWorkflowGenerationProvider>.Instance);

        unconfigured.IsConfigured.Should().BeFalse();
    }

    [UnitTest]
    public async Task BedrockWorkflowProvider_WithBoxedNumericToolArgs_ParsesWithoutError()
    {
        // Regression for honua-server#1760: the Converse adapter can surface tool-call arguments as
        // boxed CLR numbers (int/long/decimal/...) rather than JsonElements. WriteValue must emit those
        // as JSON numbers; previously decimal/byte/short/uint/ulong fell through to the stringifying
        // default branch, producing "n" for numeric DTO fields and a "could not be parsed" failure.
        var arguments = new Dictionary<string, object?>
        {
            ["status"] = "unsupported",
            ["rationale"] = "No vector-tile node is available.",
            ["unmappedRequests"] = new object?[] { "Generate vector tiles" },
            // Numeric primitives that previously hit the stringifying default branch.
            ["diagnostics"] = new Dictionary<string, object?>
            {
                ["intValue"] = 1,
                ["longValue"] = 2L,
                ["decimalValue"] = 3.5m,
                ["byteValue"] = (byte)4,
                ["shortValue"] = (short)5,
                ["uintValue"] = (uint)6,
                ["ulongValue"] = (ulong)7,
                ["floatValue"] = 8.5f,
                ["doubleValue"] = 9.5d
            }
        };

        var factory = FakeWorkflowFactoryReturning(arguments);
        var provider = new BedrockWorkflowGenerationProvider(
            WorkflowGenerationConfiguration.BedrockProviderId,
            factory,
            BedrockOptions(),
            NullLogger<BedrockWorkflowGenerationProvider>.Instance);

        var result = await provider.GenerateAsync(WorkflowProviderRequest());

        result.Status.ToString().ToLowerInvariant().Should().Be("unsupported");
        result.Rationale.Should().NotBe("Provider response could not be parsed.");
    }

    [UnitTest]
    public async Task BedrockWorkflowProvider_WhenFirstToolCallIsMissing_RetriesOnce()
    {
        // First turn returns no forced tool call (a transient bad turn); the provider must re-issue the
        // request exactly once and succeed on the second valid tool call (honua-server#1760 hardening).
        var goodArgs = new Dictionary<string, object?>
        {
            ["status"] = "unsupported",
            ["rationale"] = "Second attempt succeeded.",
            ["unmappedRequests"] = new object?[] { "anything" }
        };

        var factory = Substitute.For<IBedrockChatClientFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_ => new SequencedChatClient(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "no tool call")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-2", "emit_workflow", goodArgs)]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                }));

        var provider = new BedrockWorkflowGenerationProvider(
            WorkflowGenerationConfiguration.BedrockProviderId,
            factory,
            BedrockOptions(),
            NullLogger<BedrockWorkflowGenerationProvider>.Instance);

        var result = await provider.GenerateAsync(WorkflowProviderRequest());

        result.Status.ToString().ToLowerInvariant().Should().Be("unsupported");
        result.Rationale.Should().Be("Second attempt succeeded.");
    }

    private static WorkflowGenerationProviderRequest WorkflowProviderRequest() =>
        new()
        {
            Prompt = "build a workflow",
            Registry = new WorkflowNodeRegistrySnapshot
            {
                RegistryVersion = "test",
                GeneratedAt = DateTimeOffset.UnixEpoch,
                Providers = [],
                Nodes = []
            }
        };

    private static IBedrockChatClientFactory FakeWorkflowFactoryReturning(IDictionary<string, object?> arguments)
    {
        var factory = Substitute.For<IBedrockChatClientFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                var call = new FunctionCallContent("call-1", "emit_workflow", arguments);
                return new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            });

        return factory;
    }

    private static IOptions<WorkflowGenerationConfiguration> BedrockOptions() =>
        Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.BedrockProviderId,
            MaxRepairAttempts = 0,
            Providers =
            {
                [WorkflowGenerationConfiguration.BedrockProviderId] = new WorkflowGenerationProviderOptions
                {
                    Model = "test-bedrock-model",
                    Region = "us-west-2",
                    MaxTokens = 2048,
                    TimeoutSeconds = 30
                }
            }
        });

    /// <summary>Builds a factory whose chat client returns a single forced tool-call carrying <paramref name="toolJson"/>.</summary>
    private static IBedrockChatClientFactory FakeFactoryReturning(
        string toolJson,
        out StrongBox<string?> capturedModel,
        out StrongBox<string?> capturedRegion)
    {
        var modelBox = new StrongBox<string?>();
        var regionBox = new StrongBox<string?>();
        capturedModel = modelBox;
        capturedRegion = regionBox;

        using var document = JsonDocument.Parse(toolJson);
        var arguments = JsonElementToArguments(document.RootElement);

        var factory = Substitute.For<IBedrockChatClientFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                modelBox.Value = callInfo.ArgAt<string>(0);
                regionBox.Value = callInfo.ArgAt<string>(1);
                var call = new FunctionCallContent("call-1", "emit_document", arguments);
                return new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
                {
                    FinishReason = ChatFinishReason.ToolCalls
                });
            });

        return factory;
    }

    private static Dictionary<string, object?> JsonElementToArguments(JsonElement element)
    {
        var map = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }

        return map;
    }

    /// <summary>Minimal <see cref="IChatClient"/> that returns a fixed response — stands in for Bedrock.</summary>
    private sealed class FakeChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(response);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// <see cref="IChatClient"/> that returns each supplied response in order across successive
    /// <see cref="GetResponseAsync"/> calls — used to exercise the retry-once path.
    /// </summary>
    private sealed class SequencedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
