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
    public async Task BedrockWorkflowProvider_WhenResponseTruncatedAtMaxTokens_ReturnsTruncationError()
    {
        // Regression for #1760: large workflow prompts (vector-tiles, geocoding) overflow MaxTokens,
        // so Bedrock returns a truncated tool-call. The Converse adapter maps StopReason.Max_tokens to
        // ChatFinishReason.Length; the provider must surface an actionable truncation message instead of
        // the opaque generic "Provider response could not be parsed." (#1979 added this to the
        // OpenAI/Anthropic providers but skipped the Bedrock provider.)
        var factory = Substitute.For<IBedrockChatClientFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                // A tool-call whose JSON arguments were cut off mid-object — what a truncated
                // Converse response actually surfaces — paired with FinishReason == Length.
                var truncated = new FunctionCallContent(
                    "call-1",
                    "emit_workflow",
                    new Dictionary<string, object?> { ["status"] = "generated", ["graph"] = null });
                return new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [truncated]))
                {
                    FinishReason = ChatFinishReason.Length
                });
            });

        var provider = new BedrockWorkflowGenerationProvider(
            WorkflowGenerationConfiguration.BedrockProviderId,
            factory,
            BedrockOptions(),
            NullLogger<BedrockWorkflowGenerationProvider>.Instance);

        var result = await provider.GenerateAsync(new WorkflowGenerationProviderRequest
        {
            Prompt = "Generate vector tiles (PMTiles) for maui-roads and publish as a tile service.",
            Registry = WorkflowRegistrySnapshot()
        });

        result.Status.Should().Be(WorkflowGenerationStatus.Error);
        result.ErrorMessage.Should().Contain("truncated");
        result.ErrorMessage.Should().Contain("MaxTokens");
        result.ErrorMessage.Should().NotContain("could not be parsed");
    }

    private static WorkflowNodeRegistrySnapshot WorkflowRegistrySnapshot() => new()
    {
        RegistryVersion = "test-registry-1",
        GeneratedAt = DateTimeOffset.UnixEpoch,
        Providers = [],
        Nodes =
        [
            new WorkflowNodeDefinition
            {
                NodeTypeId = "process:data-management.copy-features",
                ProviderId = "test",
                RuntimeKind = WorkflowNodeRuntimeKind.Geoprocessing,
                Title = "Copy Features",
                Description = "Copy features",
                Category = "transform",
                ParameterSchemas = [],
                CapabilityFlags = new WorkflowNodeCapabilityFlags { Executable = true },
                RuntimeHints = new WorkflowNodeRuntimeHints()
            }
        ]
    };

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
}
