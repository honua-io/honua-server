// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Discovery;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Ai.Tests.Capabilities;

/// <summary>
/// Composition conformance for ADR-0058 B2 (#2334): the served <c>/mcp</c> catalog
/// must be bound to the unified capability registry — no tool or resource is served
/// without a registry descriptor — and the geospatial-mcp manifest emitter must
/// project its tool roster from the registry rather than a hand-maintained list.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpRegistryCompositionTests
{
    private static readonly CapabilityRegistry Registry = new();

    [UnitTest]
    public void ConformantServedSurface_IsBoundToRegistry_NoDrift()
    {
        // A real (registry-backed) served surface: every advertised tool name and
        // resource URI has a registry descriptor, so the composition check is clean.
        var surface = BuildConformantSurface();

        McpRegistryCompositionValidator.FindDrift(surface, Registry).Should().BeEmpty(
            "every served /mcp tool and resource URI must have a capability-registry descriptor");
    }

    [UnitTest]
    public void ServedToolsAndResourceFamilies_AreASubsetOfTheRegistryRoster()
    {
        var surface = BuildConformantSurface();

        var registryToolNames = Registry.All
            .Where(d => d.McpToolName is not null)
            .Select(d => d.McpToolName!)
            .ToHashSet(StringComparer.Ordinal);
        surface.ToolHandlers.Select(t => t.Name)
            .Should().OnlyContain(n => registryToolNames.Contains(n),
                "no served /mcp tool may exist without a registry descriptor");

        var registryFamilies = Registry.All
            .Where(d => d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal))
            .Select(d => d.Id[CapabilityRegistry.McpResourceIdPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        surface.Resources.Select(r => r.Family)
            .Should().OnlyContain(f => registryFamilies.Contains(f),
                "no served /mcp resource family may exist without a registry descriptor");
    }

    [UnitTest]
    public void OrphanTool_WithoutRegistryDescriptor_IsDetectedAsDrift()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [
                new ListCapabilitiesTool(jobService, NullLogger<ListCapabilitiesTool>.Instance),
                new StubOrphanTool(),
            ],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        McpRegistryCompositionValidator.FindDrift(surface, Registry)
            .Should().ContainSingle()
            .Which.Should().Contain(StubOrphanTool.OrphanName);
    }

    [UnitTest]
    public void OrphanResource_WithoutRegistryDescriptor_IsDetectedAsDrift()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [new ListCapabilitiesTool(jobService, NullLogger<ListCapabilitiesTool>.Instance)],
            [new StubOrphanResource()],
            NullLogger<McpDataAccessSurface>.Instance);

        McpRegistryCompositionValidator.FindDrift(surface, Registry)
            .Should().ContainSingle()
            .Which.Should().Contain(StubOrphanResource.OrphanUriTemplate);
    }

    [UnitTest]
    public async Task StartupCheck_Throws_OnDrift_WhenRegistryBindingEnabled()
    {
        var check = new McpRegistryBindingStartupCheck(
            new McpDataAccessSurface([new StubOrphanTool()], [], NullLogger<McpDataAccessSurface>.Instance),
            Registry,
            Options.Create(new CapabilityRegistryBindingOptions { RegistryBinding = true }));

        var act = () => check.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not bound to the capability registry*");
    }

    [UnitTest]
    public async Task StartupCheck_Skips_WhenRegistryBindingDisabled()
    {
        var check = new McpRegistryBindingStartupCheck(
            new McpDataAccessSurface([new StubOrphanTool()], [], NullLogger<McpDataAccessSurface>.Instance),
            Registry,
            Options.Create(new CapabilityRegistryBindingOptions { RegistryBinding = false }));

        // With the gate off, the drifted surface must not fail startup.
        await check.StartAsync(CancellationToken.None);
    }

    [UnitTest]
    public async Task StartupCheck_ProductionOperationsComposition_WithPublishOperationsEnabled_StartsCleanly()
    {
        // honua-server#3428 (remainder recorded by #4738): the documented
        // Mcp:PublishOperations:Enabled=true switch composed the canonical operation
        // catalog into tools/list, and the default-on registry binding check then
        // rejected every catalog-published honua_op_* / honua_admin_* tool, so the
        // server failed to start. Compose the operations toolset the way Program.cs
        // does (durable proposal store registered first, so the admin API and
        // connect/import families join) and run the real startup check over it.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:PublishOperations:Enabled"] = "true",
            })
            .Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IOperationProposalStore>());
        services.AddOperationsToolset(configuration, environment);
        services.AddAdminAccessOperations();
        McpServiceCollectionExtensions.AddMcpPublishedOperationTools(services, configuration);
        await using var provider = services.BuildServiceProvider();

        var surface = new McpDataAccessSurface(
            Honua.Server.Tests.Features.Protocols.Mcp.McpTaxonomyAlignmentTests.BuildTools(),
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            toolSources: provider.GetServices<IMcpToolSource>());

        // Non-vacuous: the composition really publishes Studio, legacy-admin and
        // Admin-family operation tools that have no static registry descriptor.
        var registryToolNames = Registry.All
            .Where(d => d.McpToolName is not null)
            .Select(d => d.McpToolName!)
            .ToHashSet(StringComparer.Ordinal);
        var published = (await surface.GetAllToolsAsync())
            .OfType<PublishedOperationTool>()
            .Select(tool => tool.Name)
            .Where(name => !registryToolNames.Contains(name))
            .ToArray();
        published.Should().Contain(name => name.StartsWith("honua_op_studio_", StringComparison.Ordinal));
        published.Should().Contain(name => name.StartsWith(PublishedOperationTool.AdminNamePrefix, StringComparison.Ordinal));

        var check = new McpRegistryBindingStartupCheck(
            surface,
            Registry,
            Options.Create(new CapabilityRegistryBindingOptions { RegistryBinding = true }));

        await check.Invoking(c => c.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync("catalog-published tools are bound through their canonical operation descriptor");
    }

    [UnitTest]
    public async Task OrphanRuntimeTool_FromANonCatalogToolSource_IsStillDetectedAsDrift()
    {
        // Operation-catalog provenance binds only tools projected from an operation
        // descriptor. Any other runtime tool source must still have a registry
        // descriptor, so the gate keeps failing fast on an orphan dynamic tool.
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var surface = new McpDataAccessSurface(
            [new ListCapabilitiesTool(jobService, NullLogger<ListCapabilitiesTool>.Instance)],
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            toolSources: [new FixedToolSource(new StubOrphanTool())]);

        (await McpRegistryCompositionValidator.FindDriftAsync(surface, Registry))
            .Should().ContainSingle()
            .Which.Should().Contain(StubOrphanTool.OrphanName);

        var check = new McpRegistryBindingStartupCheck(
            surface,
            Registry,
            Options.Create(new CapabilityRegistryBindingOptions { RegistryBinding = true }));
        await check.Invoking(c => c.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*" + StubOrphanTool.OrphanName + "*");
    }

    [UnitTest]
    public void EmitterTools_AreProjectedFromRegistry_AndTitleCaseTheWorkflowFamily()
    {
        // The emitter's advertised-tool roster is exactly the registry's static /mcp
        // tool set (both directions), with the workflow family title-cased from the
        // registry Category — proving the manifest catalog cannot drift from the
        // registry.
        var registryTools = Registry.All
            .Where(d => d.McpToolName is not null)
            .Where(d => !d.IsDynamic)
            .ToDictionary(d => d.McpToolName!, StringComparer.Ordinal);

        var emitted = CapabilityManifestEmitter.Tools;
        emitted.Select(t => t.AdvertisedName).Should().BeEquivalentTo(registryTools.Keys,
            "the emitted manifest must advertise exactly the registry's static /mcp tool roster");

        foreach (var tool in emitted)
        {
            var descriptor = registryTools[tool.AdvertisedName];
            tool.StandardName.Should().Be(descriptor.StandardName);
            tool.WorkflowFamily.Should().Be(TitleCase(descriptor.Category),
                $"emitted workflowFamily for '{tool.AdvertisedName}' must be the title-cased registry Category");
        }
    }

    private static string TitleCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static McpDataAccessSurface BuildConformantSurface()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        return new McpDataAccessSurface(
            [
                new ValidatePlanTool(jobService, NullLogger<ValidatePlanTool>.Instance),
                new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance),
                new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance),
                new ResolveEntityTool(jobService, NullLogger<ResolveEntityTool>.Instance),
                new ListCapabilitiesTool(jobService, NullLogger<ListCapabilitiesTool>.Instance),
            ],
            [
                new JobStatusResource(jobService, NullLogger<JobStatusResource>.Instance),
                new WorkspaceResource(jobService, NullLogger<WorkspaceResource>.Instance),
                new FeatureCatalogResource(jobService, NullLogger<FeatureCatalogResource>.Instance),
            ],
            NullLogger<McpDataAccessSurface>.Instance);
    }

    /// <summary>A runtime tool source that is not backed by the operation catalog.</summary>
    private sealed class FixedToolSource(params IMcpTool[] tools) : IMcpToolSource
    {
        public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IMcpTool>>(tools);
    }

    /// <summary>A tool with a name no capability-registry descriptor advertises.</summary>
    private sealed class StubOrphanTool : IMcpTool
    {
        public const string OrphanName = "honua_not_in_registry";

        public string Name => OrphanName;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

        public McpToolDescriptor Describe() => new() { Name = OrphanName };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>A resource template no capability-registry descriptor advertises.</summary>
    private sealed class StubOrphanResource : IMcpResource
    {
        public const string OrphanUriTemplate = "honua://mystery/{mysteryId}";

        public string Family => "mystery";

        public IReadOnlyList<McpResourceDescriptor> Describe() => [];

        public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() =>
            [new McpResourceTemplateDescriptor { UriTemplate = OrphanUriTemplate, Name = "Mystery" }];

        public bool CanHandle(string uri) => false;

        public Task<McpResourcesReadResult> ReadAsync(
            HttpContext httpContext, string uri, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
