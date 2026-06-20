// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
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

    // Builds the live service over the REAL WorkflowGenerationService so the test
    // exercises the full seam (provider selection + node-registry grounding + the
    // hard validation gate), with only the provider faked.
    private static LivePlanAnalysisService CreateLiveService(IWorkflowGenerationProvider fakeProvider)
    {
        var registry = Substitute.For<IWorkflowNodeRegistry>();
        registry.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(Snapshot());
        foreach (var node in Snapshot().Nodes)
        {
            registry.GetNodeAsync(node.NodeTypeId, Arg.Any<CancellationToken>()).Returns(node);
        }

        var generationService = new WorkflowGenerationService(
            new[] { fakeProvider },
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

    private static IConfiguration BuildConfiguration(bool enabled, string defaultProvider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowGeneration:Enabled"] = enabled ? "true" : "false",
                ["WorkflowGeneration:DefaultProvider"] = defaultProvider
            })
            .Build();

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
}
