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
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
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
    public async Task ProductionOperationsComposition_ByDefault_PublishesExactlyTheAuditedAdminProjection()
    {
        // honua-server#3363 (coordinator ruling 2026-09-14): eligible Admin operation
        // publication is on by default for the production composition — no
        // Mcp:PublishOperations configuration at all. The committed projection manifest is
        // the contract: its 66 rows are the Admin API and access catalogs minus the
        // audited exclusions (digest below). The closed operator roster adds four tools
        // (server status, connection create, connection test, import-from-URL) without
        // enabling the full catalog. Other admin.* providers stay opt-in.
        const int ExpectedManifestTools = 66;
        const int ExpectedRosterAdditions = 4;
        const int ExpectedPublishedTools = ExpectedManifestTools + ExpectedRosterAdditions;
        const int ExpectedExclusions = 15;
        const string ExpectedExclusionDigest = "62eb9da003bacee33a5972b31527e5a7c964a8f60308414a33ce6087eb4efae2";

        await using var provider = BuildProductionOperationsComposition(new Dictionary<string, string?>());
        var surface = BuildOperationsSurface(provider);
        var published = await PublishedToolNamesAsync(surface);

        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Honua.TestKit.RepositoryPaths.Resolve("docs", "gis", "data", "admin-mcp-projection-manifest.json")));
        var manifestToolNames = manifest.RootElement.GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("toolName").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var manifestExclusions = manifest.RootElement.GetProperty("exclusions");

        // One comparison so a drift reports every measured count at once.
        new
        {
            Published = published.Length,
            ManifestRows = manifestToolNames.Length,
            Exclusions = AdminMcpOperationExclusions.All.Count,
            ManifestExclusions = manifestExclusions.GetProperty("operations").GetArrayLength(),
        }.Should().BeEquivalentTo(new
        {
            Published = ExpectedPublishedTools,
            ManifestRows = ExpectedManifestTools,
            Exclusions = ExpectedExclusions,
            ManifestExclusions = ExpectedExclusions,
        });

        var rosterAdditions = new[]
        {
            "honua_admin_connections_create",
            "honua_admin_connections_test",
            "honua_admin_import_upload_url",
            "honua_admin_server_status",
        };
        published.Should().Equal(
            manifestToolNames.Concat(rosterAdditions).Order(StringComparer.Ordinal).ToArray(),
            "the default production composition publishes the committed audited Admin projection plus the closed operator roster");
        manifestToolNames.Should().HaveCount(ExpectedManifestTools);
        rosterAdditions.Should().HaveCount(ExpectedRosterAdditions);
        published.Should().Contain(rosterAdditions);
        AdminMcpOperationExclusions.Digest.Should().Be(ExpectedExclusionDigest);
        manifestExclusions.GetProperty("digest").GetString().Should().Be(ExpectedExclusionDigest);
        published.Should().NotIntersectWith(
            AdminMcpOperationExclusions.All.Select(exclusion => exclusion.ToolName),
            "audited one-time-secret, secret-input and browser-session operations never publish");
        published.Should().NotContain("honua_admin_connections_test_draft",
            "connection draft tests accept connection credentials and are an audited secret-input exclusion");

        // #3813/#3819: publication does not restore default full enumeration — no
        // published Admin tool joins the bounded default view.
        published.Should().OnlyContain(
            name => Honua.Ai.Protocols.Mcp.Views.McpWorkflowViewCatalog.Default.FindStageIndex(name) < 0);

        var check = new McpRegistryBindingStartupCheck(
            surface,
            Registry,
            Options.Create(new CapabilityRegistryBindingOptions { RegistryBinding = true }));
        await check.Invoking(c => c.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync("the default published projection is bound through its operation descriptors");
    }

    [UnitTest]
    public async Task ProductionOperationsComposition_WithFullCatalogOptIn_PublishesEveryNonExcludedOperation()
    {
        // The explicit full-catalog opt-in keeps its pre-#3363 meaning: every eligible
        // descriptor publishes except the audited exclusions and hand-authored duplicates.
        const int ExpectedCatalogAdminOperations = 110;
        const int ExpectedExclusionsInCatalog = 7;
        const int ExpectedPublishedAdminTools = 103;

        await using var provider = BuildProductionOperationsComposition(new Dictionary<string, string?>
        {
            ["Mcp:PublishOperations:Enabled"] = "true",
        });
        var published = await PublishedToolNamesAsync(BuildOperationsSurface(provider));
        var publishedAdminTools = published
            .Where(name => name.StartsWith(PublishedOperationTool.AdminNamePrefix, StringComparison.Ordinal))
            .ToArray();
        var catalogOperationIds = (await provider.GetRequiredService<IOperationCatalog>().GetSnapshotAsync())
            .Operations
            .Select(descriptor => descriptor.OperationId)
            .ToArray();
        var catalogAdminOperationIds = catalogOperationIds
            .Where(operationId => operationId.StartsWith("admin.", StringComparison.Ordinal))
            .ToArray();

        // Exclusions that are not catalog operations (embed keys, browser sessions) can
        // never publish; the ones that are catalog operations are withheld.
        new
        {
            CatalogAdminOperations = catalogAdminOperationIds.Length,
            ExclusionsInCatalog = catalogAdminOperationIds.Count(AdminMcpOperationExclusions.ContainsOperation),
            PublishedAdminTools = publishedAdminTools.Length,
        }.Should().BeEquivalentTo(new
        {
            CatalogAdminOperations = ExpectedCatalogAdminOperations,
            ExclusionsInCatalog = ExpectedExclusionsInCatalog,
            PublishedAdminTools = ExpectedPublishedAdminTools,
        });
        publishedAdminTools.Should().BeEquivalentTo(
            catalogAdminOperationIds
                .Where(operationId => !AdminMcpOperationExclusions.ContainsOperation(operationId))
                .Select(PublishedOperationTool.ProjectName));

        // honua_studio_propose_publication owns Studio publication with owner authorization;
        // the generic projection of the same operation is never published.
        catalogOperationIds.Should().Contain("studio.content.create-publication-request");
        published.Should().NotContain(PublishedOperationTool.ProjectName("studio.content.create-publication-request"));
        published.Should().Contain(name => name.StartsWith(PublishedOperationTool.NamePrefix, StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task ProductionOperationsComposition_WithFullCatalogOptIn_PublishesNoSecretLikeInputs()
    {
        // honua-server#4880: the full-catalog opt-in must not publish a tool whose input schema
        // accepts secret material. Secret references (names ending in "Reference") are allowed.
        await using var provider = BuildProductionOperationsComposition(new Dictionary<string, string?>
        {
            ["Mcp:PublishOperations:Enabled"] = "true",
        });
        var tools = (await BuildOperationsSurface(provider).GetAllToolsAsync())
            .OfType<PublishedOperationTool>()
            .ToArray();

        var secretInputs = tools
            .SelectMany(tool => InputPropertyNames(tool.Describe().InputSchema)
                .Where(IsSecretLikeInputName)
                .Select(name => $"{tool.Name}.{name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        tools.Should().NotBeEmpty();
        secretInputs.Should().BeEmpty("published operation tools accept secret references, never secret values");
    }

    private static readonly System.Text.RegularExpressions.Regex SecretLikeInputName = new(
        "password|passphrase|secret|credential|token|api[-_]?key|private[-_]?key",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static bool IsSecretLikeInputName(string name) =>
        SecretLikeInputName.IsMatch(name) &&
        !name.EndsWith("Reference", StringComparison.Ordinal) &&
        !name.EndsWith("Type", StringComparison.Ordinal);

    private static IEnumerable<string> InputPropertyNames(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            yield break;
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in InputPropertyNames(property.Value))
                    yield return $"{property.Name}.{nested}";
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            foreach (var nested in InputPropertyNames(items))
                yield return nested;
        }
    }

    [UnitTest]
    public async Task ProductionOperationsComposition_WithAdminProjectionDisabled_PublishesNoOperationTools()
    {
        // The operator opt-out stays honored after #3363 turned the audited projection on by default.
        await using var provider = BuildProductionOperationsComposition(new Dictionary<string, string?>
        {
            ["Mcp:PublishOperations:AdminProjection"] = "false",
        });

        (await PublishedToolNamesAsync(BuildOperationsSurface(provider))).Should().BeEmpty();
    }

    private static McpDataAccessSurface BuildOperationsSurface(IServiceProvider provider) => new(
        Honua.Server.Tests.Features.Protocols.Mcp.McpTaxonomyAlignmentTests.BuildTools(),
        [],
        NullLogger<McpDataAccessSurface>.Instance,
        toolSources: provider.GetServices<IMcpToolSource>());

    private static async Task<string[]> PublishedToolNamesAsync(McpDataAccessSurface surface) =>
        (await surface.GetAllToolsAsync())
            .OfType<PublishedOperationTool>()
            .Select(tool => tool.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static ServiceProvider BuildProductionOperationsComposition(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IOperationProposalStore>());
        services.AddOperationsToolset(configuration, environment);
        services.AddAdminAccessOperations();
        McpServiceCollectionExtensions.AddMcpPublishedOperationTools(services, configuration);
        return services.BuildServiceProvider();
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
