// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.WorkflowGeneration;

/// <summary>
/// Exercises the natural-language workflow generation orchestration with the deterministic
/// fixture provider: the prompt grounds in a node registry, the canned proposal passes the
/// <see cref="WorkflowPackageGraphValidator"/> hard gate, and the providers surface reflects
/// configuration — all without a live model.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class WorkflowGenerationServiceTests
{
    // The deterministic "generated" fixture (workflow-generation-contract-v1.json) keyed by this prompt.
    private const string GeneratedPrompt =
        "Nightly: copy new assessor parcels into a working layer, stamp a reviewed flag, then compute parcel area.";

    private const string ClarificationPrompt =
        "Copy the parcels into the public works layer and publish it.";

    private const string UnsupportedPrompt =
        "Pull the assessor CSV and then send a carrier pigeon to the county office.";

    // Authoring/publishing prompts that previously had no grounded node type and were refused
    // (honua-server#1759). Each now maps to an authoring:* node and must yield an accepted graph.
    public static IEnumerable<object[]> AuthoringPrompts()
    {
        yield return ["Publish maui-parcels as a FeatureServer and OGC API Features service.", "authoring:publish.multiprotocol"];
        yield return ["Style the parcels layer as a choropleth by assessed value.", "authoring:style.choropleth"];
        yield return ["Build a web map of parcels, zoning, and flood with a legend.", "authoring:map.web-map"];
        yield return ["Create a dashboard with a map and a chart of permits by district.", "authoring:dashboard.build"];
        yield return ["Create a STAC catalog over the Maui drone imagery.", "authoring:stac.catalog"];
    }

    [UnitTest]
    public async Task GenerateAsync_DeterministicProvider_ReturnsGraphThatPassesTheValidationGate()
    {
        var service = CreateService(enabled: true);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = GeneratedPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.Generated);
        result.Graph.Should().NotBeNull();
        result.Graph!.Nodes.Should().HaveCount(3);
        result.Validation.Should().NotBeNull();
        result.Validation!.IsValid.Should().BeTrue();
        result.Provider.Should().Be(WorkflowGenerationConfiguration.DeterministicProviderId);
        result.RegistryVersion.Should().Be("test-registry-1");
    }

    // honua-server#1759: each core authoring/publishing prompt (publish/style/map/dashboard/STAC)
    // must now ground in a real authoring:* node, generate a graph, and pass the validation hard
    // gate — i.e. be *accepted*, not refused as "unsupported". The registry snapshot is built from
    // the production AuthoringWorkflowNodeProvider so this proves the actual grounding surface (and
    // its required-parameter contract) accepts the fixture proposals.
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", Tiers.Fast)]
    [MemberData(nameof(AuthoringPrompts))]
    public async Task GenerateAsync_AuthoringPrompt_ReturnsAcceptedGraphForRefusedCapability(
        string prompt,
        string expectedNodeTypeId)
    {
        var service = CreateService(enabled: true);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = prompt });

        result.Status.Should().Be(
            WorkflowGenerationStatus.Generated,
            "the authoring capability '{0}' is now grounded and must not be refused",
            expectedNodeTypeId);
        result.Graph.Should().NotBeNull();
        result.Graph!.Nodes.Should().ContainSingle(node => node.NodeTypeId == expectedNodeTypeId);
        result.Validation.Should().NotBeNull();
        result.Validation!.IsValid.Should().BeTrue("the proposed authoring graph must pass the server validation hard gate");
        result.UnmappedRequests.Should().BeNullOrEmpty("an accepted authoring proposal has no unmapped requests");
    }

    [UnitTest]
    public async Task GenerateAsync_AmbiguousPrompt_ReturnsClarificationWithoutAGraph()
    {
        var service = CreateService(enabled: true);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = ClarificationPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.NeedsClarification);
        result.Graph.Should().BeNull();
        result.Clarifications.Should().NotBeEmpty();
    }

    [UnitTest]
    public async Task GenerateAsync_UnmappableStep_ReturnsUnsupportedAndSurfacesTheUnmappedRequest()
    {
        var service = CreateService(enabled: true);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = UnsupportedPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.Unsupported);
        result.Graph.Should().BeNull();
        result.UnmappedRequests.Should().NotBeEmpty();
    }

    [UnitTest]
    public async Task GenerateAsync_WhenDisabled_ReportsUnsupportedRatherThanCallingAProvider()
    {
        var service = CreateService(enabled: false);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = GeneratedPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.Unsupported);
        result.Graph.Should().BeNull();
    }

    [UnitTest]
    public async Task GenerateAsync_OversizedPrompt_ReturnsErrorBeforeRegistryOrProviderCalls()
    {
        var registry = Substitute.For<IWorkflowNodeRegistry>();
        var provider = Substitute.For<IWorkflowGenerationProvider>();
        provider.ProviderId.Returns(WorkflowGenerationConfiguration.DeterministicProviderId);
        provider.IsConfigured.Returns(true);
        var service = CreateService(
            enabled: true,
            registry,
            [provider],
            maxPromptCharacters: 10);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = new string('x', 11) });

        result.Status.Should().Be(WorkflowGenerationStatus.Error);
        result.Rationale.Should().Contain(nameof(WorkflowGenerationConfiguration.MaxPromptCharacters));
        result.Rationale.Should().NotContain(new string('x', 11));
        _ = registry.DidNotReceive().GetSnapshotAsync(Arg.Any<CancellationToken>());
        _ = provider.DidNotReceive().GenerateAsync(
            Arg.Any<WorkflowGenerationProviderRequest>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task GetProvidersAsync_WhenEnabled_ReportsTheDeterministicProviderAsAvailable()
    {
        var service = CreateService(enabled: true);

        var providers = await service.GetProvidersAsync();

        providers.Enabled.Should().BeTrue();
        providers.DefaultProvider.Should().Be(WorkflowGenerationConfiguration.DeterministicProviderId);
        providers.Providers.Should().Contain(descriptor =>
            descriptor.Id == WorkflowGenerationConfiguration.DeterministicProviderId && descriptor.Available);
    }

    [UnitTest]
    public async Task GetProvidersAsync_WhenDisabled_ReportsTheFeatureOff()
    {
        var service = CreateService(enabled: false);

        var providers = await service.GetProvidersAsync();

        providers.Enabled.Should().BeFalse();
    }

    private static WorkflowGenerationService CreateService(
        bool enabled,
        IWorkflowNodeRegistry? registry = null,
        IReadOnlyList<IWorkflowGenerationProvider>? providers = null,
        int maxPromptCharacters = 16_000)
    {
        registry ??= Substitute.For<IWorkflowNodeRegistry>();
        registry.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(_ => Snapshot());

        providers ??= new IWorkflowGenerationProvider[]
        {
            new DeterministicWorkflowGenerationProvider(
                new WorkflowGenerationFixtureCatalog(),
                NullLogger<DeterministicWorkflowGenerationProvider>.Instance)
        };

        var configuration = Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = enabled,
            DefaultProvider = WorkflowGenerationConfiguration.DeterministicProviderId,
            MaxPromptCharacters = maxPromptCharacters
        });

        return new WorkflowGenerationService(
            providers,
            registry,
            configuration,
            NullLogger<WorkflowGenerationService>.Instance);
    }

    // A snapshot carrying the node types the deterministic fixtures use: the geoprocessing nodes the
    // "generated" ETL fixture references, plus the real authoring/publishing palette grounded by the
    // production AuthoringWorkflowNodeProvider (honua-server#1759). Real authoring nodes — not stubs —
    // so the validation gate exercises their actual executable flag and required-parameter contract.
    private static WorkflowNodeRegistrySnapshot Snapshot()
    {
        var nodes = new List<WorkflowNodeDefinition>
        {
            Node("process:data-management.copy-features"),
            Node("process:data-management.calculate-field"),
            Node("process:geometry.area")
        };

        // The provider has no dependencies and lists its nodes synchronously; the task is already completed.
        var authoringNodes = new AuthoringWorkflowNodeProvider().ListNodesAsync().GetAwaiter().GetResult();
        nodes.AddRange(authoringNodes);

        return new WorkflowNodeRegistrySnapshot
        {
            RegistryVersion = "test-registry-1",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Providers = [],
            Nodes = nodes
        };
    }

    private static WorkflowNodeDefinition Node(string nodeTypeId) => new()
    {
        NodeTypeId = nodeTypeId,
        ProviderId = "test",
        RuntimeKind = WorkflowNodeRuntimeKind.Geoprocessing,
        Title = nodeTypeId,
        Description = nodeTypeId,
        Category = "transform",
        ParameterSchemas = [],
        CapabilityFlags = new WorkflowNodeCapabilityFlags { Executable = true },
        RuntimeHints = new WorkflowNodeRuntimeHints()
    };
}
