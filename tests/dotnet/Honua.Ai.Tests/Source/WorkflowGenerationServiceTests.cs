// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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

    [Fact]
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

    [Fact]
    public async Task GenerateAsync_AmbiguousPrompt_ReturnsClarificationWithoutAGraph()
    {
        var service = CreateService(enabled: true);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = ClarificationPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.NeedsClarification);
        result.Graph.Should().BeNull();
        result.Clarifications.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_UnmappableStep_ReturnsUnsupportedAndSurfacesTheUnmappedRequest()
    {
        var service = CreateService(enabled: true);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = UnsupportedPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.Unsupported);
        result.Graph.Should().BeNull();
        result.UnmappedRequests.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_WhenDisabled_ReportsUnsupportedRatherThanCallingAProvider()
    {
        var service = CreateService(enabled: false);

        var result = await service.GenerateAsync(new WorkflowGenerationRequest { Prompt = GeneratedPrompt });

        result.Status.Should().Be(WorkflowGenerationStatus.Unsupported);
        result.Graph.Should().BeNull();
    }

    [Fact]
    public async Task GetProvidersAsync_WhenEnabled_ReportsTheDeterministicProviderAsAvailable()
    {
        var service = CreateService(enabled: true);

        var providers = await service.GetProvidersAsync();

        providers.Enabled.Should().BeTrue();
        providers.DefaultProvider.Should().Be(WorkflowGenerationConfiguration.DeterministicProviderId);
        providers.Providers.Should().Contain(descriptor =>
            descriptor.Id == WorkflowGenerationConfiguration.DeterministicProviderId && descriptor.Available);
    }

    [Fact]
    public async Task GetProvidersAsync_WhenDisabled_ReportsTheFeatureOff()
    {
        var service = CreateService(enabled: false);

        var providers = await service.GetProvidersAsync();

        providers.Enabled.Should().BeFalse();
    }

    private static WorkflowGenerationService CreateService(bool enabled)
    {
        var registry = Substitute.For<IWorkflowNodeRegistry>();
        registry.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(Snapshot());

        var providers = new IWorkflowGenerationProvider[]
        {
            new DeterministicWorkflowGenerationProvider(
                new WorkflowGenerationFixtureCatalog(),
                NullLogger<DeterministicWorkflowGenerationProvider>.Instance)
        };

        var configuration = Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = enabled,
            DefaultProvider = WorkflowGenerationConfiguration.DeterministicProviderId
        });

        return new WorkflowGenerationService(
            providers,
            registry,
            configuration,
            NullLogger<WorkflowGenerationService>.Instance);
    }

    // A snapshot carrying exactly the node types the generated fixture uses, with no required parameters,
    // so the fixture graph passes the validation gate (known types, acyclic control chain, unique ids).
    private static WorkflowNodeRegistrySnapshot Snapshot() => new()
    {
        RegistryVersion = "test-registry-1",
        GeneratedAt = DateTimeOffset.UnixEpoch,
        Providers = [],
        Nodes =
        [
            Node("process:data-management.copy-features"),
            Node("process:data-management.calculate-field"),
            Node("process:geometry.area")
        ]
    };

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
