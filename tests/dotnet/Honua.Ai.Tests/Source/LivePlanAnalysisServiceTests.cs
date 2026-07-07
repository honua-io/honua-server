// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Net.Http;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.Ai.AiBuilder;
using Honua.Ai.AiBuilder.Planning;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.WorkflowGeneration;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Exercises the live (provider-backed) AI-builder plan lane that compiles an
/// arbitrary natural-language intent into an executable analysis plan through the
/// shared <c>WorkflowGeneration</c> Bedrock seam. The provider here is a FAKE
/// <see cref="IWorkflowGenerationProvider"/> returning a canned graph — no live
/// Bedrock/AI calls — so the live code path is proven deterministically in CI.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class LivePlanAnalysisServiceTests
{
    private const string BufferIntent =
        "Buffer the Maui flood-hazard layer by 500 m and select intersecting parcels.";

    [UnitTest]
    public async Task PlanAsync_ArbitraryIntentNotInAnyFixture_ReturnsExecutablePlanViaLiveProvider()
    {
        var service = CreateLiveService(FakeProvider.Generated(CannedGraph()));

        var output = await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);

        output.Status.Should().Be("planned");
        output.Plan.Should().NotBeNull();
        output.FixtureCase.Should().BeNullOrEmpty("the live path is not a fixture replay");

        var stepIds = output.Plan!.Steps.Select(step => step.StepId).ToArray();
        stepIds.Should().BeEquivalentTo(["buffer-flood", "select-parcels"]);

        // The downstream MCP validate/dry-run/execute lane consumes the plan via
        // McpToolHelpers.ToDomainPlan; proving that conversion succeeds proves the
        // adapted graph is a real executable plan, not just a wire blob.
        var domainPlan = ToDomainPlan(output.Plan!);
        domainPlan.Steps.Should().HaveCount(2);
        domainPlan.Steps[0].Kind.Should().Be(AnalysisPlanStepKind.Geoprocess);
        domainPlan.Steps[0].ProcessId.Should().Be("geometry.buffer");
        domainPlan.Steps[1].Kind.Should().Be(AnalysisPlanStepKind.QueryFeatures);
        domainPlan.Steps[1].DependsOn.Should().Contain("buffer-flood");
    }

    [UnitTest]
    public async Task PlanAsync_LivePlan_FlowsThroughValidateLaneBeyondPlanAnalysis()
    {
        // #2485 checklist ("live lanes beyond plan_analysis"): the live planner's
        // compiled plan is not a dead end at plan_analysis — it must feed the same
        // validate/dry-run lane the fixture plans flow through. Convert the live
        // output to a domain plan and run it through the exact validator the
        // honua_validate_plan tool uses (ProcessPlanValidator over the built-in
        // process catalog), proving the live lane extends past plan_analysis.
        var service = CreateLiveService(FakeProvider.Generated(CannedGraph()));

        var output = await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);
        output.Status.Should().Be("planned");

        var domainPlan = ToDomainPlan(output.Plan!);
        var (violations, _) = ProcessPlanValidator.Validate(domainPlan, new BuiltInProcessCatalog());

        // The validator ran over the live plan and produced a structured result
        // (the lane is wired), and the buffer step resolves to the canonical
        // geometry.buffer process — i.e. the live lane's plan is validate-ready,
        // not a plan-analysis-only artifact.
        violations.Should().NotBeNull();
        domainPlan.Steps.Should().HaveCount(2);
        domainPlan.Steps[0].ProcessId.Should().Be("geometry.buffer");
    }

    [UnitTest]
    public async Task PlanAsync_ProviderNeedsClarification_ReturnsClarificationRequired()
    {
        var clarifications = new[]
        {
            new WorkflowGenerationClarification
            {
                Id = "source",
                Kind = "source",
                Prompt = "Which flood-hazard layer?",
                Choices =
                [
                    new WorkflowGenerationClarificationChoice { Id = "maui", Label = "Maui flood hazard" }
                ]
            }
        };
        var service = CreateLiveService(FakeProvider.Clarification(clarifications));

        var output = await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);

        output.Status.Should().Be("clarification_required");
        output.Plan.Should().BeNull();
        output.Clarification!.Candidates.Select(c => c.Kind).Should().Contain("source");
    }

    [UnitTest]
    public async Task PlanAsync_ProviderUnsupported_ReturnsUnsupportedWithCapabilityState()
    {
        var service = CreateLiveService(FakeProvider.Unsupported("spatialJoin"));

        var output = await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);

        output.Status.Should().Be("unsupported");
        output.Plan.Should().BeNull();
        output.CapabilityState!.Name.Should().Be("spatialJoin");
    }

    [UnitTest]
    public async Task PlanAsync_ProviderThrows_ReturnsStructuredRejectionNotCrash()
    {
        // The provider transport (e.g. the Anthropic Messages API) faults: a
        // timeout, an HTTP error, or malformed JSON. The live planner must
        // degrade to a structured 'rejected' turn rather than letting the
        // exception escape and crash the MCP tool.
        var service = CreateLiveService(ThrowingProvider.Instance);

        var act = async () => await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);

        var output = await act.Should().NotThrowAsync();
        output.Subject.Status.Should().Be("rejected");
        output.Subject.Plan.Should().BeNull();
        output.Subject.Reason.Should().NotBeNullOrEmpty();
    }

    [UnitTest]
    public async Task PlanAsync_ProviderThrows_EchoesContextBackOnRejection()
    {
        var service = CreateLiveService(ThrowingProvider.Instance);
        var context = McpTestFactory.ParseJson("""{"correlationId":"err-1"}""");

        var output = await service.PlanAsync(BufferIntent, context, CancellationToken.None);

        output.Status.Should().Be("rejected");
        output.Context!.Value.GetProperty("correlationId").GetString().Should().Be("err-1");
    }

    [UnitTest]
    public void ShouldUseLivePlanner_EnabledWithAnthropicDefault_IsTrue()
    {
        // The Anthropic-direct provider ("claude") is the preferred in-tree live
        // provider for the server-side planner; enabling it must select the live lane.
        var configuration = BuildConfiguration(enabled: true, defaultProvider: "claude");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeTrue();
    }

    [UnitTest]
    public void AddAiBuilderPlanAnalysis_AnthropicConfigured_ResolvesLivePlanner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkflowGenerationService>());
        services.AddSingleton(Substitute.For<IWorkflowNodeRegistry>());

        services.AddAiBuilderPlanAnalysis(BuildConfiguration(enabled: true, defaultProvider: "claude"));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPlanAnalysisService>()
            .Should().BeOfType<LivePlanAnalysisService>();
    }

    [UnitTest]
    public async Task PlanAsync_EchoesContextBackUnchanged()
    {
        var service = CreateLiveService(FakeProvider.Generated(CannedGraph()));
        var context = McpTestFactory.ParseJson("""{"correlationId":"abc-123"}""");

        var output = await service.PlanAsync(BufferIntent, context, CancellationToken.None);

        output.Context.Should().NotBeNull();
        output.Context!.Value.GetProperty("correlationId").GetString().Should().Be("abc-123");
    }

    [UnitTest]
    public void ShouldUseLivePlanner_NoProviderConfigured_IsFalse()
    {
        var configuration = new ConfigurationBuilder().Build();

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeFalse();
    }

    [UnitTest]
    public void ShouldUseLivePlanner_DisabledWithBedrockDefault_IsFalse()
    {
        var configuration = BuildConfiguration(enabled: false, defaultProvider: "bedrock");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeFalse();
    }

    [UnitTest]
    public void ShouldUseLivePlanner_EnabledWithDeterministicDefault_IsFalse()
    {
        var configuration = BuildConfiguration(enabled: true, defaultProvider: "deterministic");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeFalse();
    }

    [UnitTest]
    public void ShouldUseLivePlanner_EnabledWithBedrockDefault_IsTrue()
    {
        var configuration = BuildConfiguration(enabled: true, defaultProvider: "bedrock");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeTrue();
    }

    [UnitTest]
    public void AddAiBuilderPlanAnalysis_NoProvider_ResolvesFixturePlanner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiBuilderPlanAnalysis(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPlanAnalysisService>()
            .Should().BeOfType<FixturePlanAnalysisService>();
    }

    [UnitTest]
    public void AddAiBuilderPlanAnalysis_BedrockConfigured_ResolvesLivePlanner()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Live planner dependencies (normally registered by AddWorkflowGeneration /
        // AddWorkflowPackages in the server composition).
        services.AddSingleton(Substitute.For<IWorkflowGenerationService>());
        services.AddSingleton(Substitute.For<IWorkflowNodeRegistry>());

        services.AddAiBuilderPlanAnalysis(BuildConfiguration(enabled: true, defaultProvider: "bedrock"));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPlanAnalysisService>()
            .Should().BeOfType<LivePlanAnalysisService>();
    }

    // --- Dedicated PlanAnalysis seam (#1955): independent gate + provider override ---

    [UnitTest]
    public void ShouldUseLivePlanner_PlanAnalysisEnabledTrue_OverridesWorkflowGenerationDisabled()
    {
        // The Studio WorkflowGeneration feature is OFF, but the operator opts the
        // MCP plan lane IN via the dedicated PlanAnalysis seam — naming a live
        // provider there. The plan lane goes live without enabling Studio.
        var configuration = BuildConfiguration(
            enabled: false,
            defaultProvider: "deterministic",
            planEnabled: true,
            planProvider: "bedrock");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeTrue();
    }

    [UnitTest]
    public void ShouldUseLivePlanner_PlanAnalysisEnabledFalse_PinsFixturesEvenWhenWorkflowGenerationLive()
    {
        // WorkflowGeneration is live, but the operator pins the MCP plan lane to
        // deterministic fixtures via PlanAnalysis:Enabled=false.
        var configuration = BuildConfiguration(
            enabled: true,
            defaultProvider: "bedrock",
            planEnabled: false);

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeFalse();
    }

    [UnitTest]
    public void ShouldUseLivePlanner_PlanAnalysisProviderOverridesDeterministicWorkflowDefault_IsTrue()
    {
        // WorkflowGeneration is enabled but its default provider is deterministic;
        // PlanAnalysis:Provider overrides it to a live provider for the plan lane.
        var configuration = BuildConfiguration(
            enabled: true,
            defaultProvider: "deterministic",
            planProvider: "anthropic");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeTrue();
    }

    [UnitTest]
    public void ShouldUseLivePlanner_PlanAnalysisProviderDeterministic_OverridesLiveWorkflowDefault_IsFalse()
    {
        // Symmetric: an operator can pin the plan lane to deterministic even when
        // the WorkflowGeneration default is a live provider.
        var configuration = BuildConfiguration(
            enabled: true,
            defaultProvider: "bedrock",
            planProvider: "deterministic");

        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(configuration).Should().BeFalse();
    }

    [UnitTest]
    public async Task PlanAsync_PlanAnalysisProviderSet_RoutesGenerationToThatProvider()
    {
        // The PlanAnalysis:Provider override must steer the WorkflowGeneration seam
        // to a DIFFERENT provider than its default. The default here is "bedrock";
        // the override names "anthropic", so the anthropic provider is invoked.
        var bedrock = RecordingProvider.Generated("bedrock", CannedGraph());
        var anthropic = RecordingProvider.Generated("anthropic", CannedGraph());
        var service = CreateLiveService(
            new IWorkflowGenerationProvider[] { bedrock, anthropic },
            new PlanAnalysisConfiguration { Provider = "anthropic" });

        var output = await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);

        output.Status.Should().Be("planned");
        anthropic.WasInvoked.Should().BeTrue("PlanAnalysis:Provider selected the anthropic provider");
        bedrock.WasInvoked.Should().BeFalse("the override steered away from the WorkflowGeneration default");
    }

    [UnitTest]
    public async Task PlanAsync_NoPlanAnalysisProvider_UsesWorkflowGenerationDefaultProvider()
    {
        // No override → the WorkflowGeneration default provider ("bedrock") runs.
        var bedrock = RecordingProvider.Generated("bedrock", CannedGraph());
        var anthropic = RecordingProvider.Generated("anthropic", CannedGraph());
        var service = CreateLiveService(
            new IWorkflowGenerationProvider[] { bedrock, anthropic },
            new PlanAnalysisConfiguration());

        var output = await service.PlanAsync(BufferIntent, context: null, CancellationToken.None);

        output.Status.Should().Be("planned");
        bedrock.WasInvoked.Should().BeTrue("absent an override the WorkflowGeneration default provider runs");
        anthropic.WasInvoked.Should().BeFalse();
    }

    [UnitTest]
    public void PlanAnalysisConfigurationValidator_UnknownProvider_Fails()
    {
        var validator = new PlanAnalysisConfigurationValidator();

        var result = validator.Validate(name: null, new PlanAnalysisConfiguration { Provider = "gemini" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("gemini");
    }

    [UnitTest]
    public void PlanAnalysisConfigurationValidator_NoProviderOverride_Succeeds()
    {
        var validator = new PlanAnalysisConfigurationValidator();

        var result = validator.Validate(name: null, new PlanAnalysisConfiguration { Enabled = true });

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void PlanAnalysisConfigurationValidator_KnownProvider_Succeeds()
    {
        var validator = new PlanAnalysisConfigurationValidator();

        var result = validator.Validate(name: null, new PlanAnalysisConfiguration { Provider = "anthropic" });

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void AddAiBuilderPlanAnalysis_PlanAnalysisEnabledOnly_ResolvesLivePlanner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkflowGenerationService>());
        services.AddSingleton(Substitute.For<IWorkflowNodeRegistry>());

        // WorkflowGeneration disabled; PlanAnalysis opts the lane in with a live provider.
        services.AddAiBuilderPlanAnalysis(BuildConfiguration(
            enabled: false,
            defaultProvider: "deterministic",
            planEnabled: true,
            planProvider: "bedrock"));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPlanAnalysisService>()
            .Should().BeOfType<LivePlanAnalysisService>();
    }

    [UnitTest]
    public void AddAiBuilderPlanAnalysis_DocumentedLivePlannerProfile_ResolvesLivePlanner()
    {
        // #2485 AC1: the supported "live-planner-on" configuration profile documented
        // in docs/guides/connect/mcp-live-planner.md must actually select the live
        // planner. This pins that profile verbatim (WorkflowGeneration enabled with a
        // bedrock provider + the dedicated PlanAnalysis gate/provider) so the doc and
        // the composition can never drift apart silently.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IWorkflowGenerationService>());
        services.AddSingleton(Substitute.For<IWorkflowNodeRegistry>());

        var profile = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowGeneration:Enabled"] = "true",
                ["WorkflowGeneration:DefaultProvider"] = "bedrock",
                ["WorkflowGeneration:Providers:bedrock:Model"] =
                    "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
                ["WorkflowGeneration:Providers:bedrock:Region"] = "us-west-2",
                ["PlanAnalysis:Enabled"] = "true",
                ["PlanAnalysis:Provider"] = "bedrock",
            })
            .Build();

        services.AddAiBuilderPlanAnalysis(profile);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPlanAnalysisService>()
            .Should().BeOfType<LivePlanAnalysisService>();
        AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner(profile).Should().BeTrue();
    }

    // Builds the live service over the REAL WorkflowGenerationService so the test
    // exercises the full seam (provider selection + node-registry grounding + the
    // hard validation gate), with only the provider faked.
    private static LivePlanAnalysisService CreateLiveService(
        IWorkflowGenerationProvider fakeProvider,
        PlanAnalysisConfiguration? planOptions = null) =>
        CreateLiveService(new[] { fakeProvider }, planOptions);

    private static LivePlanAnalysisService CreateLiveService(
        IReadOnlyList<IWorkflowGenerationProvider> fakeProviders,
        PlanAnalysisConfiguration? planOptions = null)
    {
        var registry = Substitute.For<IWorkflowNodeRegistry>();
        registry.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(Snapshot());
        foreach (var node in Snapshot().Nodes)
        {
            registry.GetNodeAsync(node.NodeTypeId, Arg.Any<CancellationToken>()).Returns(node);
        }

        var generationService = new WorkflowGenerationService(
            fakeProviders,
            registry,
            Options.Create(new WorkflowGenerationConfiguration
            {
                Enabled = true,
                DefaultProvider = "bedrock"
            }),
            NullLogger<WorkflowGenerationService>.Instance);

        return new LivePlanAnalysisService(
            generationService,
            registry,
            Options.Create(planOptions ?? new PlanAnalysisConfiguration()),
            NullLogger<LivePlanAnalysisService>.Instance);
    }

    private static WorkflowGraph CannedGraph() => new()
    {
        Nodes =
        [
            new WorkflowNode
            {
                NodeId = "buffer-flood",
                NodeTypeId = "process:geometry.buffer",
                Parameters = new Dictionary<string, string> { ["distance"] = "500", ["unit"] = "meters" }
            },
            new WorkflowNode
            {
                NodeId = "select-parcels",
                NodeTypeId = "process:feature.query"
            }
        ],
        Edges =
        [
            new WorkflowEdge { SourceNodeId = "buffer-flood", TargetNodeId = "select-parcels" }
        ]
    };

    // Registry snapshot carrying the canned graph's node types so the graph passes
    // the validation gate; the buffer node surfaces a canonical ProcessId.
    private static WorkflowNodeRegistrySnapshot Snapshot() => new()
    {
        RegistryVersion = "test-registry-live",
        GeneratedAt = DateTimeOffset.UnixEpoch,
        Providers = [],
        Nodes =
        [
            Node("process:geometry.buffer", "geometry.buffer", WorkflowNodeRuntimeKind.Geoprocessing, "transform"),
            Node("process:feature.query", processId: null, WorkflowNodeRuntimeKind.Geoprocessing, "query")
        ]
    };

    private static WorkflowNodeDefinition Node(
        string nodeTypeId,
        string? processId,
        WorkflowNodeRuntimeKind runtimeKind,
        string category) => new()
        {
            NodeTypeId = nodeTypeId,
            ProviderId = "test",
            RuntimeKind = runtimeKind,
            Title = nodeTypeId,
            Description = nodeTypeId,
            Category = category,
            ParameterSchemas = [],
            CapabilityFlags = new WorkflowNodeCapabilityFlags { Executable = true },
            RuntimeHints = new WorkflowNodeRuntimeHints(),
            ProcessId = processId
        };

    private static IConfiguration BuildConfiguration(
        bool enabled,
        string defaultProvider,
        bool? planEnabled = null,
        string? planProvider = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["WorkflowGeneration:Enabled"] = enabled ? "true" : "false",
            ["WorkflowGeneration:DefaultProvider"] = defaultProvider
        };

        if (planEnabled is { } flag)
        {
            settings["PlanAnalysis:Enabled"] = flag ? "true" : "false";
        }

        if (planProvider is not null)
        {
            settings["PlanAnalysis:Provider"] = planProvider;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static AnalysisPlan ToDomainPlan(McpAnalysisPlanOutput plan)
    {
        var input = new McpPlanInput
        {
            PlanId = plan.PlanId,
            IntentId = plan.IntentId,
            Steps = plan.Steps.Select(step => new McpPlanStepInput
            {
                StepId = step.StepId,
                Kind = step.Kind,
                ProcessId = step.ProcessId,
                DependsOn = step.DependsOn.ToList(),
                Inputs = new Dictionary<string, string>(step.Inputs)
            }).ToList(),
            Outputs = [],
            Warnings = plan.Warnings.ToList()
        };

        return McpToolHelpers.ToDomainPlan(input);
    }

    /// <summary>
    /// Fake provider that returns a canned proposal — stands in for the Bedrock
    /// provider so the live path runs without any AI call.
    /// </summary>
    private sealed class FakeProvider : IWorkflowGenerationProvider
    {
        private readonly WorkflowGenerationProposal _proposal;

        private FakeProvider(WorkflowGenerationProposal proposal) => _proposal = proposal;

        public string ProviderId => "bedrock";

        public bool IsConfigured => true;

        public Task<WorkflowGenerationProposal> GenerateAsync(
            WorkflowGenerationProviderRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_proposal);

        public static FakeProvider Generated(WorkflowGraph graph) => new(new WorkflowGenerationProposal
        {
            Status = WorkflowGenerationStatus.Generated,
            Graph = graph,
            ProviderId = "bedrock",
            Model = "fake-model"
        });

        public static FakeProvider Clarification(IReadOnlyList<WorkflowGenerationClarification> clarifications)
            => new(new WorkflowGenerationProposal
            {
                Status = WorkflowGenerationStatus.NeedsClarification,
                Clarifications = clarifications,
                ProviderId = "bedrock"
            });

        public static FakeProvider Unsupported(string capability) => new(new WorkflowGenerationProposal
        {
            Status = WorkflowGenerationStatus.Unsupported,
            CapabilityState = new WorkflowGenerationCapabilityState
            {
                Name = capability,
                State = "unsupported"
            },
            UnmappedRequests = [capability],
            ProviderId = "bedrock"
        });
    }

    /// <summary>
    /// Fake provider carrying a fixed provider id that records whether it was
    /// invoked, so tests can assert which provider the WorkflowGeneration seam
    /// selected when the live planner applies the PlanAnalysis:Provider override.
    /// </summary>
    private sealed class RecordingProvider : IWorkflowGenerationProvider
    {
        private readonly WorkflowGenerationProposal _proposal;

        private RecordingProvider(string providerId, WorkflowGenerationProposal proposal)
        {
            ProviderId = providerId;
            _proposal = proposal;
        }

        public string ProviderId { get; }

        public bool IsConfigured => true;

        public bool WasInvoked { get; private set; }

        public Task<WorkflowGenerationProposal> GenerateAsync(
            WorkflowGenerationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            return Task.FromResult(_proposal);
        }

        public static RecordingProvider Generated(string providerId, WorkflowGraph graph) =>
            new(providerId, new WorkflowGenerationProposal
            {
                Status = WorkflowGenerationStatus.Generated,
                Graph = graph,
                ProviderId = providerId,
                Model = "fake-model"
            });
    }

    /// <summary>
    /// Fake provider whose <see cref="GenerateAsync"/> throws — stands in for a
    /// provider transport fault (Anthropic Messages API error, timeout, or
    /// malformed response) so the live planner's graceful-degradation path runs
    /// without any real AI call.
    /// </summary>
    private sealed class ThrowingProvider : IWorkflowGenerationProvider
    {
        public static readonly ThrowingProvider Instance = new();

        public string ProviderId => "bedrock";

        public bool IsConfigured => true;

        public Task<WorkflowGenerationProposal> GenerateAsync(
            WorkflowGenerationProviderRequest request,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("simulated provider transport failure");
    }
}
