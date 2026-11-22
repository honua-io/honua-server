// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
using Honua.Infrastructure.Services;
using Honua.Server.Tests.Features.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Ai.Tests.Capabilities;

[Protocol(TestProtocols.Mcp)]
public sealed class CapabilityRegistryConformanceTests
{
    private static readonly CapabilityRegistry Registry = new();

    // The capability roster, sourced from
    // CapabilityManifestService.BuildCapabilities (honua.capability_manifest.v1).
    // That service cannot be constructed from a unit test (its options
    // snapshot bundles internal option types in Honua.Hosting/Honua.Import that
    // are not visible here), so this roster is pinned here like the taxonomy
    // roster in McpTaxonomyAlignmentTests. Adding a capability to
    // BuildCapabilities requires adding a registry descriptor AND updating this
    // list — that is the intended freeze discipline, not incidental duplication.
    private static readonly string[] ManifestCapabilityIds =
    [
        "package.metadata-v2",
        "package.release-package",
        "package.gitops-manifest",
        "package.map",
        "package.app",
        "provider.redshift",
        "provider.snowflake",
        "provider.databricks",
        "temporal.filtering",
        "temporal.extent-discovery",
        "temporal.histogram",
        "temporal.time-series-tiles",
        "temporal.animation-api",
        "serve.3d-tiles-scene",
        "serve.i3s-scene",
        "serve.ogc-api-edr",
        "scene.catalog",
        "scene.bim-ingest",
        "scene.pointcloud-ingest",
        "sync.offline",
        "realtime.feature-streams",
        "serve.sensorthings",
        "serve.geoservices-imageserver",
        "serve.wmts",
        "serve.ogc-api-coverages",
        "alerts.geofence",
        "jobs.runner",
        "ai.spec-apply",
        "ai.grounding",
        "gitops.release-manifest",
        "admin.multi-tenancy",
        "transport.grpc",
        "transport.grpc-web",
        "transport.native-grpc",
        "transport.mcp",
        "transport.qgis",
        "security.mtls",
        "preview.file-import",
        "query.features",
        "analysis.spatial",
        "publication.metadata-release",
        "upload.file",
        "edit.features",
        "collaboration.feature-locks",
        "collaboration.feature-locks.cross-node",
        "console.content.persistent",
        "edit.geoservices-version-tokens",
        "versioning.branch",
        "operate.status",
    ];

    [UnitTest]
    public void Registry_IsNotEmpty()
    {
        Registry.All.Should().NotBeEmpty();
        Registry.All.Should().OnlyContain(d =>
            !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.Category));
    }

    [UnitTest]
    public void EveryDescriptor_ResolvesToItself_AndHasUniqueId()
    {
        var ids = Registry.All.Select(d => d.Id).ToArray();
        ids.Should().OnlyHaveUniqueItems("capability ids are the frozen primary key");

        foreach (var descriptor in Registry.All)
        {
            Registry.Find(descriptor.Id).Should().BeSameAs(descriptor,
                $"Find must round-trip the descriptor for '{descriptor.Id}'");
        }
    }

    [UnitTest]
    public void Resolve_RespectsImplementationAndOptIn_AndRejectsUnknown()
    {
        var context = CapabilityGateContext.Default;

        foreach (var descriptor in Registry.All)
        {
            var resolution = Registry.Resolve(descriptor.Id, context);
            if (descriptor.ImplementationStatus == CapabilityImplementationStatus.KnownGap)
            {
                resolution.Enabled.Should().BeFalse(
                    $"unimplemented capability '{descriptor.Id}' cannot be served");
                resolution.ReasonCode.Should().Be(CapabilityReasonCodes.NotImplemented);
            }
            else if (descriptor.Id is "serve.geoservices-imageserver")
            {
                // GA in 2026.1 (2026-10-24 operator ruling); it was already served by default.
                descriptor.Maturity.Should().Be(CapabilityMaturity.Implemented);
                descriptor.RequiresOptIn.Should().BeFalse();
                resolution.Enabled.Should().BeTrue(
                    $"GA capability '{descriptor.Id}' is served by default");
                resolution.ReasonCode.Should().BeNull();
            }
            else if (descriptor.Id is "serve.wmts" or "serve.ogc-api-coverages")
            {
                descriptor.Maturity.Should().Be(CapabilityMaturity.Preview);
                descriptor.RequiresOptIn.Should().BeFalse();
                resolution.Enabled.Should().BeTrue(
                    $"lifecycle-only Preview capability '{descriptor.Id}' is already served");
                resolution.ReasonCode.Should().BeNull();
            }
            else if (descriptor.Maturity is CapabilityMaturity.Internal or CapabilityMaturity.Experimental or CapabilityMaturity.Preview)
            {
                descriptor.RequiresOptIn.Should().BeTrue(
                    "Internal and all other Preview and Experimental capabilities retain their opt-in gate");
                // Experimental capabilities resolve disabled in the
                // default context (no experimental flags set) with the dedicated reason code.
                resolution.Enabled.Should().BeFalse(
                    $"opt-in capability '{descriptor.Id}' is off by default");
                resolution.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
            }
            else
            {
                resolution.Enabled.Should().BeTrue(
                    $"non-experimental capability '{descriptor.Id}' resolves enabled");
                resolution.ReasonCode.Should().BeNull();
            }
        }

        var unknown = Registry.Resolve("does.not.exist", context);
        unknown.Enabled.Should().BeFalse();
        unknown.ReasonCode.Should().Be(CapabilityRegistry.NotRegisteredReasonCode);
    }

    [UnitTest]
    public void InternalMultiTenancy_RetainsOptInAndEditionGates()
    {
        const string capabilityId = "admin.multi-tenancy";
        var descriptor = Registry.Find(capabilityId)!;
        descriptor.Maturity.Should().Be(CapabilityMaturity.Internal);
        descriptor.RequiresOptIn.Should().BeTrue();
        descriptor.MinimumEdition.Should().Be(HonuaEdition.Enterprise);

        var context = new CapabilityGateContext { Edition = HonuaEdition.Enterprise };
        var disabled = Registry.Resolve(capabilityId, context);
        disabled.Enabled.Should().BeFalse("the Internal relabel preserves default-off behavior even in Enterprise");
        disabled.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);

        var flags = new CapabilityFlagOptions();
        flags.Capabilities[capabilityId] = new ExperimentalCapabilityFlag { Enabled = true };
        var unlicensed = Registry.Resolve(capabilityId, new CapabilityGateContext
        {
            ExperimentalFlags = flags,
            Edition = HonuaEdition.Community,
        });
        unlicensed.Enabled.Should().BeFalse("opting in does not bypass the edition gate");
        unlicensed.ReasonCode.Should().Be(CapabilityReasonCodes.LicenseRequired);

        var enabled = Registry.Resolve(capabilityId, new CapabilityGateContext
        {
            ExperimentalFlags = flags,
            Edition = HonuaEdition.Enterprise,
        });
        enabled.Enabled.Should().BeTrue("the internal deployment retains its canonical opt-in");
        enabled.ReasonCode.Should().BeNull();
    }

    [UnitTest]
    public void LiveMcpTools_MatchRegistryToolDescriptors()
    {
        var liveToolNames = BuildLiveTools()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var registryToolNames = Registry.All
            .Where(d => d.Id.StartsWith(CapabilityRegistry.McpToolIdPrefix, StringComparison.Ordinal))
            .Select(d => d.McpToolName!)
            .ToHashSet(StringComparer.Ordinal);

        registryToolNames.Should().BeEquivalentTo(liveToolNames,
            "every live /mcp tool must have exactly one registry descriptor and vice versa");
    }

    [UnitTest]
    public void RegistryToolDescriptors_MirrorLiveWorkflowFamilies()
    {
        var liveFamilyByName = BuildLiveTools()
            .ToDictionary(t => t.Name, t => t.WorkflowFamily, StringComparer.Ordinal);

        foreach (var descriptor in Registry.All
                     .Where(d => d.Id.StartsWith(CapabilityRegistry.McpToolIdPrefix, StringComparison.Ordinal)))
        {
            descriptor.McpToolName.Should().NotBeNull();
            liveFamilyByName.TryGetValue(descriptor.McpToolName!, out var liveFamily).Should().BeTrue();
            descriptor.Category.Should().Be(liveFamily,
                $"tool '{descriptor.McpToolName}' Category must mirror the live workflow family");
        }
    }

    [UnitTest]
    public void LiveMcpResources_MatchRegistryResourceDescriptors()
    {
        var liveFamilies = BuildLiveResources()
            .Select(r => r.Family)
            .ToHashSet(StringComparer.Ordinal);

        var registryFamilies = Registry.All
            .Where(d => d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal))
            .Select(d => d.Id[CapabilityRegistry.McpResourceIdPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);

        registryFamilies.Should().BeEquivalentTo(liveFamilies,
            "every live /mcp resource family must have exactly one registry descriptor and vice versa");
    }

    [UnitTest]
    public void RegistryResourceUris_MatchLiveResourceUris()
    {
        var resources = BuildLiveResources();
        var skills = OperatingSkillsResource.Instance;
        skills.Describe().Should().NotBeEmpty();
        skills.Describe().Should().OnlyContain(d => skills.CanHandle(d.Uri)
            && d.Uri.StartsWith("skill://", StringComparison.Ordinal),
            "the embedded skill bundle owns the concrete files in its registered URI family");
        var fixedResources = resources.Where(r => r is not OperatingSkillsResource).ToArray();
        var liveUris = fixedResources.SelectMany(r => r.Describe()).Select(d => d.Uri)
            .Concat(fixedResources.SelectMany(r => r.DescribeTemplates()).Select(d => d.UriTemplate))
            .Append("skill://{skill}/{path}")
            .ToHashSet(StringComparer.Ordinal);

        var registryUris = Registry.All
            .Where(d => d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal))
            .SelectMany(d => d.ResourceUriTemplates)
            .ToHashSet(StringComparer.Ordinal);

        registryUris.Should().BeEquivalentTo(liveUris,
            "the registry resource-URI templates must mirror the live resources/list + templates surface");
    }

    [UnitTest]
    public void ManifestCapabilities_AllHaveRegistryDescriptor()
    {
        var registryManifestIds = Registry.All
            .Where(d =>
                !d.Id.StartsWith(CapabilityRegistry.McpToolIdPrefix, StringComparison.Ordinal) &&
                !d.Id.StartsWith(CapabilityRegistry.McpResourceIdPrefix, StringComparison.Ordinal) &&
                !d.Id.StartsWith(CapabilityRegistry.DataFormatIdPrefix, StringComparison.Ordinal))
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);

        registryManifestIds.Should().BeEquivalentTo(ManifestCapabilityIds,
            "every #1186 capability-manifest capability must have exactly one registry descriptor and vice versa");
    }

    [UnitTest]
    public void EntitlementBackedDescriptors_DeriveMinimumEditionFromFeatureCatalog()
    {
        foreach (var descriptor in Registry.All.Where(d => d.EntitlementKey is not null))
        {
            Honua.Core.Features.Licensing.Domain.FeatureCatalog.All
                .Select(f => f.Key)
                .Should().Contain(descriptor.EntitlementKey!,
                    $"'{descriptor.Id}' entitlement key must exist in the FeatureCatalog");

            var expectedEdition = Honua.Core.Features.Licensing.Domain.FeatureCatalog.All
                .First(f => f.Key == descriptor.EntitlementKey).MinimumEdition;
            descriptor.MinimumEdition.Should().Be(expectedEdition,
                $"'{descriptor.Id}' MinimumEdition must be derived from the FeatureCatalog");
        }
    }

    [UnitTest]
    public void SupportedFileFormats_AllHaveRegistryDescriptor()
    {
        foreach (var format in Enum.GetValues<SupportedFileFormat>())
        {
            var expectedId = CapabilityRegistry.DataFormatIdPrefix
                + format.ToString().ToLowerInvariant();
            Registry.Find(expectedId).Should().NotBeNull(
                $"import format '{format}' must have registry descriptor '{expectedId}'");
        }
    }

    [UnitTest]
    public void FormatDescriptors_MapToRealFormat()
    {
        var importFormatNames = Enum.GetValues<SupportedFileFormat>()
            .Select(f => f.ToString().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var writers = Honua.Io.Export.BuiltInExportFormats.Dispatch.Values
            .Select(format => format.ToString().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var wireResponseFormats = new HashSet<string>(StringComparer.Ordinal) { "geoarrow" };

        foreach (var descriptor in Registry.All
                     .Where(d => d.Id.StartsWith(CapabilityRegistry.DataFormatIdPrefix, StringComparison.Ordinal)))
        {
            var name = descriptor.StandardName!;
            var isWireOnly = descriptor.Category == "format" && wireResponseFormats.Contains(name);
            (importFormatNames.Contains(name) || writers.Contains(name) || isWireOnly).Should().BeTrue(
                $"format descriptor '{descriptor.Id}' must map to an import format, a built-in export writer, or a wire response format");
        }

        foreach (var name in importFormatNames.Union(writers))
        {
            AssertDirection("read", name, importFormatNames.Contains(name));
            AssertDirection("write", name, writers.Contains(name));
        }

        Registry.All.Where(d => d.Category == "format-write" && d.ImplementationStatus == CapabilityImplementationStatus.Served)
            .Select(d => d.StandardName).Should().BeEquivalentTo(writers);
        Honua.Io.Export.BuiltInExportFormats.Dispatch.Values.Distinct().Should()
            .BeEquivalentTo(Enum.GetValues<Honua.Io.Export.BuiltInExportFormat>());
    }

    [UnitTest]
    public void WarehouseProvidersAndCloudNativeFormats_AreRegisteredAcrossRegistryAndCatalog()
    {
        var catalogByKey = CapabilityKeyCatalog.All.ToDictionary(c => c.Key, StringComparer.Ordinal);

        foreach (var providerId in new[] { "provider.redshift", "provider.snowflake", "provider.databricks" })
        {
            var descriptor = Registry.Find(providerId);
            descriptor.Should().NotBeNull();
            descriptor!.Maturity.Should().Be(CapabilityMaturity.Experimental);
            catalogByKey.Should().ContainKey(providerId);
            catalogByKey[providerId].Status.Should().Be(CapabilityKeyCatalog.ExperimentalStatus);
        }

        foreach (var formatId in new[] { "format.geoparquet", "format.geoarrow" })
        {
            var descriptor = Registry.Find(formatId);
            descriptor.Should().NotBeNull();
            descriptor!.Kind.Should().Be(CapabilityKind.DataFormat);
            descriptor.Maturity.Should().Be(CapabilityMaturity.Implemented);
            catalogByKey.Should().ContainKey(formatId);
            catalogByKey[formatId].Edition.Should().Be(HonuaEdition.Community);
        }

        var geoParquetWrite = Registry.Find("format.write.geoparquet");
        geoParquetWrite.Should().NotBeNull();
        geoParquetWrite!.Maturity.Should().Be(CapabilityMaturity.Implemented);
        geoParquetWrite.ImplementationStatus.Should().Be(CapabilityImplementationStatus.Served);
    }

    private static void AssertDirection(string direction, string name, bool served)
    {
        var descriptor = Registry.Find($"format.{direction}.{name}");
        descriptor.Should().NotBeNull();
        descriptor!.Maturity.Should().Be(served ? CapabilityMaturity.Implemented : CapabilityMaturity.Planned);
        descriptor.ImplementationStatus.Should().Be(served ? CapabilityImplementationStatus.Served : CapabilityImplementationStatus.KnownGap);
        var resolution = Registry.Resolve(descriptor.Id, CapabilityGateContext.Default);
        resolution.Enabled.Should().Be(served);
        resolution.ReasonCode.Should().Be(served ? null : CapabilityReasonCodes.NotImplemented);
        FormatCapabilityGate.Evaluate($"{direction}.{name}").IsBlocked.Should().Be(!served);
    }

    // -----------------------------------------------------------------------
    // The shared taxonomy roster covers all advertised tools except server status.
    // Compare that same surface, plus status, against the capability registry.
    // -----------------------------------------------------------------------
    private static IMcpTool[] BuildLiveTools() =>
    [
        ..McpTaxonomyAlignmentTests.BuildTools(),
        new AdminServerStatusTool(),
    ];

    private static IMcpResource[] BuildLiveResources()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var reportService = Substitute.For<IAnalysisReportService>();
        var services = Substitute.For<IPublishedServiceStore>();
        var deployments = Substitute.For<IDeploymentStore>();
        var drafts = new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System);
        return
        [
            OperatingSkillsResource.Instance,
            new TemporaryFileResource(Substitute.For<IGeoprocessingJobService>(), Options.Create(new TemporaryFileOptions()), NullLogger<TemporaryFileResource>.Instance),
            new JobStatusResource(jobService, NullLogger<JobStatusResource>.Instance),
            new JobResultsResource(jobService, NullLogger<JobResultsResource>.Instance),
            new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance),
            new AnalysisReportResource(reportService, NullLogger<AnalysisReportResource>.Instance),
            new WorkspaceResource(jobService, NullLogger<WorkspaceResource>.Instance),
            new ProcessCatalogResource(jobService, NullLogger<ProcessCatalogResource>.Instance),
            new FeatureCatalogResource(jobService, NullLogger<FeatureCatalogResource>.Instance),
            new OpsHealthResource(NullLogger<OpsHealthResource>.Instance),
            new OpsFindingsResource(NullLogger<OpsFindingsResource>.Instance),
            new PublishedServiceResource(
                services, deployments, jobService, NullLogger<PublishedServiceResource>.Instance),
            new DeploymentResource(deployments, jobService, NullLogger<DeploymentResource>.Instance),
            new MapResource(deployments, drafts, jobService, NullLogger<MapResource>.Instance),
            new AppResource(deployments, drafts, jobService, NullLogger<AppResource>.Instance),
            new PromotionSurfaceIndexResource(
                services, deployments, jobService, NullLogger<PromotionSurfaceIndexResource>.Instance),
        ];
    }

    private sealed class AdminServerStatusTool : IMcpTool
    {
        public string Name => "honua_admin_server_status";

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Results;

        public McpToolDescriptor Describe() => new() { Name = Name };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext,
            JsonElement? arguments,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
