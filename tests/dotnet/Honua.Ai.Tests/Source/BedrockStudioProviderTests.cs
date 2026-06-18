// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.AnalysisGeneration;
using Honua.Ai.AppGeneration;
using Honua.Ai.DashboardGeneration;
using Honua.Ai.FormGeneration;
using Honua.Ai.MapGeneration;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.QueryGeneration;
using Honua.Ai.ReportGeneration;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Configuration;
using Honua.Core.Features.Publishing.Dashboards;
using Honua.Core.Features.Publishing.Reports;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Geoprocessing;
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
    public async Task MapService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        const string ToolJson = """
        { "status": "needs-clarification", "rationale": "Which basemap?", "clarifications": [] }
        """;

        var factory = FakeFactoryReturning(ToolJson, out var capturedModel, out var capturedRegion);
        var service = new MapGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<MapGenerationService>.Instance);

        var result = await service.GenerateAsync(new MapGenerationRequest { Prompt = "fleet map" });

        // The Endpoint guard no longer rejects Bedrock (which has no endpoint) with "unsupported";
        // the call routed through the Converse backend instead of the HTTP-only path.
        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Model.Should().Be("test-bedrock-model");
        capturedModel.Value.Should().Be("test-bedrock-model");
        capturedRegion.Value.Should().Be("us-west-2");
    }

    [UnitTest]
    public async Task AppService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        const string ToolJson = """
        { "status": "needs-clarification", "rationale": "Which components?", "clarifications": [] }
        """;

        var factory = FakeFactoryReturning(ToolJson, out var capturedModel, out var capturedRegion);
        var service = new AppGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<AppGenerationService>.Instance);

        var result = await service.GenerateAsync(new AppGenerationRequest { Prompt = "field app" });

        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Model.Should().Be("test-bedrock-model");
        capturedModel.Value.Should().Be("test-bedrock-model");
        capturedRegion.Value.Should().Be("us-west-2");
    }

    [UnitTest]
    public async Task FormService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        const string ToolJson = """
        { "status": "needs-clarification", "rationale": "Which fields?", "clarifications": [] }
        """;

        var factory = FakeFactoryReturning(ToolJson, out var capturedModel, out var capturedRegion);
        var service = new FormGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            limitsOptions: Options.Create(new LimitsOptions()),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<FormGenerationService>.Instance);

        var result = await service.GenerateAsync(new FormGenerationRequest { Prompt = "inspection form" });

        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Model.Should().Be("test-bedrock-model");
        capturedModel.Value.Should().Be("test-bedrock-model");
        capturedRegion.Value.Should().Be("us-west-2");
    }

    [UnitTest]
    public async Task AnalysisService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        const string ToolJson = """
        { "status": "needs-clarification", "rationale": "Which layers?", "clarifications": [] }
        """;

        var factory = FakeFactoryReturning(ToolJson, out var capturedModel, out var capturedRegion);
        var service = new AnalysisGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            processCatalog: new BuiltInProcessCatalog(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<AnalysisGenerationService>.Instance);

        var result = await service.GenerateAsync(new AnalysisGenerationRequest { Prompt = "buffer parcels by 100m" });

        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Model.Should().Be("test-bedrock-model");
        capturedModel.Value.Should().Be("test-bedrock-model");
        capturedRegion.Value.Should().Be("us-west-2");
    }

    [UnitTest]
    public async Task QueryService_WithBedrockProvider_RoutesThroughConverseAndSurfacesTheProposal()
    {
        const string ToolJson = """
        { "status": "needs-clarification", "rationale": "Which layer?", "clarifications": [] }
        """;

        var factory = FakeFactoryReturning(ToolJson, out var capturedModel, out var capturedRegion);
        var service = new QueryGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: BedrockOptions(),
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: factory,
            logger: NullLogger<QueryGenerationService>.Instance);

        var result = await service.GenerateAsync(new QueryGenerationRequest { Prompt = "parcels within 500m of rivers" });

        result.Status.Should().Be("needs-clarification");
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Model.Should().Be("test-bedrock-model");
        capturedModel.Value.Should().Be("test-bedrock-model");
        capturedRegion.Value.Should().Be("us-west-2");
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
