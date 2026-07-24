// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Ai.Tests.Capabilities;

/// <summary>
/// Runtime registry↔catalog conformance check for the unified capability registry
/// (ADR-0058, honua-server B1 / #2333). It fails if the <see cref="CapabilityRegistry"/>
/// drifts from the live surface it mirrors: a live <c>/mcp</c> tool/resource or a
/// #1186 capability-manifest capability with no registry descriptor, or a registry
/// descriptor that resolves to nothing real. Modelled on
/// <c>McpTaxonomyAlignmentTests</c> and <c>FeatureCatalogDriftTests</c>.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class CapabilityRegistryConformanceTests
{
    private static readonly CapabilityRegistry Registry = new();

    // The #1186 capability roster, sourced from
    // CapabilityManifestService.BuildCapabilities (honua.capability_manifest.v1).
    // The #1186 service cannot be constructed from a unit test (its options
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
        "temporal.filtering",
        "temporal.extent-discovery",
        "temporal.histogram",
        "temporal.time-series-tiles",
        "sync.offline",
        "realtime.feature-streams",
        "alerts.geofence",
        "jobs.runner",
        "ai.spec-apply",
        "ai.grounding",
        "ai.workflow-generation",
        "gitops.release-manifest",
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
        "versioning.branch",
        "operate.status",
    ];

    // Format descriptors that intentionally exist beyond the SupportedFileFormat
    // import enum: writer/codec-backed formats (EsriJsonWkbWriter, WKT/WKB codecs).
    private static readonly HashSet<string> NonImportFormatNames =
        new(StringComparer.Ordinal) { "esrijson", "wkb" };

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
    public void Resolve_EnablesEveryRegisteredCapability_AndRejectsUnknown()
    {
        var context = CapabilityGateContext.Default;

        foreach (var descriptor in Registry.All)
        {
            var resolution = Registry.Resolve(descriptor.Id, context);
            if (descriptor.Maturity == CapabilityMaturity.Experimental)
            {
                // Since the #2346 T10 flip, experimental capabilities resolve disabled in the
                // default context (no experimental flags set) with the dedicated reason code.
                resolution.Enabled.Should().BeFalse(
                    $"experimental capability '{descriptor.Id}' is off by default");
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
        var liveUris = resources.SelectMany(r => r.Describe()).Select(d => d.Uri)
            .Concat(resources.SelectMany(r => r.DescribeTemplates()).Select(d => d.UriTemplate))
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

        foreach (var descriptor in Registry.All
                     .Where(d => d.Id.StartsWith(CapabilityRegistry.DataFormatIdPrefix, StringComparison.Ordinal)))
        {
            var name = descriptor.Id[CapabilityRegistry.DataFormatIdPrefix.Length..];
            (importFormatNames.Contains(name) || NonImportFormatNames.Contains(name)).Should().BeTrue(
                $"format descriptor '{descriptor.Id}' must map to a SupportedFileFormat member or a known writer/codec format");
        }
    }

    // -----------------------------------------------------------------------
    // Live-surface construction — mirrors McpTaxonomyAlignmentTests.BuildTools /
    // BuildResources, extended with the two package-review tools those tests
    // construct separately, so this enumerates the full advertised tool roster.
    // -----------------------------------------------------------------------
    private static IMcpTool[] BuildLiveTools()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var groundingService = Substitute.For<IGroundingService>();
        var reviewService = Substitute.For<IPackageReviewService>();
        return
        [
            new ValidatePlanTool(jobService, NullLogger<ValidatePlanTool>.Instance),
            new DryRunPlanTool(jobService, NullLogger<DryRunPlanTool>.Instance),
            new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance),
            new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance),
            new ProposeOperationTool(NullLogger<ProposeOperationTool>.Instance),
            new PublishServiceTool(NullLogger<PublishServiceTool>.Instance),
            new PublishResultTool(jobService, NullLogger<PublishResultTool>.Instance),
            new CreateMapPackageTool(jobService, NullLogger<CreateMapPackageTool>.Instance),
            new CreateAppPackageTool(jobService, NullLogger<CreateAppPackageTool>.Instance),
            new PlanAnalysisTool(
                Substitute.For<Honua.Ai.AiBuilder.Planning.IPlanAnalysisService>(),
                jobService,
                NullLogger<PlanAnalysisTool>.Instance),
            new GroundCandidatesTool(groundingService, jobService, NullLogger<GroundCandidatesTool>.Instance),
            new ClarifyIntentTool(groundingService, jobService, NullLogger<ClarifyIntentTool>.Instance),
            new GeocodeTool(jobService, NullLogger<GeocodeTool>.Instance),
            new GeocodeAddressesTool(jobService, NullLogger<GeocodeAddressesTool>.Instance),
            new IngestDatasetTool(jobService, NullLogger<IngestDatasetTool>.Instance),
            new RouteTool(jobService, NullLogger<RouteTool>.Instance),
            new OpsHealthTool(NullLogger<OpsHealthTool>.Instance),
            new OpsFindingsTool(NullLogger<OpsFindingsTool>.Instance),
            new AlertEventsTool(NullLogger<AlertEventsTool>.Instance),
            new OperateEventsTool(NullLogger<OperateEventsTool>.Instance),
            new PlatformReleaseStatusTool(NullLogger<PlatformReleaseStatusTool>.Instance),
            new DeployOperationsTool(NullLogger<DeployOperationsTool>.Instance),
            new SupportedOperationKindsTool(NullLogger<SupportedOperationKindsTool>.Instance),
            new ProposeRollbackTool(NullLogger<ProposeRollbackTool>.Instance),
            new ValidatePackageTool(reviewService, jobService, NullLogger<ValidatePackageTool>.Instance),
            new PreviewPackageTool(reviewService, jobService, NullLogger<PreviewPackageTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.ListLayersTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.ListLayersTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.DescribeLayerTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.DescribeLayerTool>.Instance),
            new ListJobsTool(jobService, NullLogger<ListJobsTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.QueryFeaturesTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.QueryFeaturesTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.RenderMapTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.RenderMapTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.GetStyleTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.GetStyleTool>.Instance),
            new Honua.Ai.Protocols.Mcp.MapTools.ApplyStylePresetTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.MapTools.ApplyStylePresetTool>.Instance),
            new Honua.Ai.Protocols.Mcp.Discovery.ResolveEntityTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.Discovery.ResolveEntityTool>.Instance),
            new Honua.Ai.Protocols.Mcp.Discovery.ListCapabilitiesTool(
                jobService, NullLogger<Honua.Ai.Protocols.Mcp.Discovery.ListCapabilitiesTool>.Instance),
            new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance),
            new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance),
            new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance),
            new ValidateStudioDraftTool(jobService, NullLogger<ValidateStudioDraftTool>.Instance),
            new PreviewStudioDraftTool(jobService, NullLogger<PreviewStudioDraftTool>.Instance),
            new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance),
            new RemoveStudioLayerTool(jobService, NullLogger<RemoveStudioLayerTool>.Instance),
            new SetStudioLayerStyleTool(jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            new SetStudioViewTool(jobService, NullLogger<SetStudioViewTool>.Instance),
            new AddStudioWidgetTool(jobService, NullLogger<AddStudioWidgetTool>.Instance),
            new RemoveStudioWidgetTool(jobService, NullLogger<RemoveStudioWidgetTool>.Instance),
            new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance),
        ];
    }

    private static IMcpResource[] BuildLiveResources()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var reportService = Substitute.For<IAnalysisReportService>();
        var services = Substitute.For<IPublishedServiceStore>();
        var deployments = Substitute.For<IDeploymentStore>();
        return
        [
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
            new MapPackageResource(deployments, jobService, NullLogger<MapPackageResource>.Instance),
            new AppPackageResource(deployments, jobService, NullLogger<AppPackageResource>.Instance),
            new PromotionSurfaceIndexResource(
                services, deployments, jobService, NullLogger<PromotionSurfaceIndexResource>.Instance),
        ];
    }
}
