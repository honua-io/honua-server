// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Studio.Drafts;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpTaxonomyAlignmentTests
{
    internal static IMcpTool[] BuildTools()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var groundingService = Substitute.For<IGroundingService>();
        var reviewService = Substitute.For<Honua.Core.Features.PackageReview.Abstractions.IPackageReviewService>();
        return
        [
            new ValidatePlanTool(jobService, NullLogger<ValidatePlanTool>.Instance),
            new DryRunPlanTool(jobService, NullLogger<DryRunPlanTool>.Instance),
            new ValidatePackageTool(reviewService, jobService, NullLogger<ValidatePackageTool>.Instance),
            new PreviewPackageTool(reviewService, jobService, NullLogger<PreviewPackageTool>.Instance),
            new ExecutePlanTool(jobService, NullLogger<ExecutePlanTool>.Instance),
            new CancelJobTool(jobService, NullLogger<CancelJobTool>.Instance),
            new PublishServiceTool(NullLogger<PublishServiceTool>.Instance),
            new PublishResultTool(jobService, NullLogger<PublishResultTool>.Instance),
            new CreateMapPackageTool(
                jobService,
                new MapPackageDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
                new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System),
                NullLogger<CreateMapPackageTool>.Instance),
            new CreateAppPackageTool(
                jobService,
                new AppPackageDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
                new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System),
                NullLogger<CreateAppPackageTool>.Instance),
            new PlanAnalysisTool(
                Substitute.For<Honua.Ai.AiBuilder.Planning.IPlanAnalysisService>(),
                jobService,
                NullLogger<PlanAnalysisTool>.Instance),
            new GroundCandidatesTool(groundingService, jobService, NullLogger<GroundCandidatesTool>.Instance),
            new ClarifyIntentTool(groundingService, jobService, NullLogger<ClarifyIntentTool>.Instance),
            new GeocodeTool(jobService, NullLogger<GeocodeTool>.Instance),
            new GeocodeAddressesTool(jobService, NullLogger<GeocodeAddressesTool>.Instance),
            new IngestDatasetTool(NullLogger<IngestDatasetTool>.Instance),
            new RouteTool(jobService, NullLogger<RouteTool>.Instance),
            new OpsHealthTool(NullLogger<OpsHealthTool>.Instance),
            new OpsFindingsTool(NullLogger<OpsFindingsTool>.Instance),
            new AlertEventsTool(NullLogger<AlertEventsTool>.Instance),
            new OperateEventsTool(NullLogger<OperateEventsTool>.Instance),
            new PlatformReleaseStatusTool(NullLogger<PlatformReleaseStatusTool>.Instance),
            new DeployOperationsTool(NullLogger<DeployOperationsTool>.Instance),
            new SupportedOperationKindsTool(NullLogger<SupportedOperationKindsTool>.Instance),
            new ProposeRollbackTool(NullLogger<ProposeRollbackTool>.Instance),
            new ProposeFindingTool(NullLogger<ProposeFindingTool>.Instance),
            new ProposeDeployPlanTool(NullLogger<ProposeDeployPlanTool>.Instance),
            new ProposeDeployOperationTool(NullLogger<ProposeDeployOperationTool>.Instance),
            new ProposeMetadataReleaseTool(NullLogger<ProposeMetadataReleaseTool>.Instance),
            new ProposePlatformReleaseConvergenceTool(NullLogger<ProposePlatformReleaseConvergenceTool>.Instance),
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
            new SaveStudioVersionTool(jobService, NullLogger<SaveStudioVersionTool>.Instance),
            new GetStudioVersionTool(jobService, NullLogger<GetStudioVersionTool>.Instance),
            new ReopenStudioVersionTool(jobService, NullLogger<ReopenStudioVersionTool>.Instance),
            new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance),
            new RemoveStudioLayerTool(jobService, NullLogger<RemoveStudioLayerTool>.Instance),
            new SetStudioLayerStyleTool(jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            new SetStudioLayerVisibilityTool(jobService, NullLogger<SetStudioLayerVisibilityTool>.Instance),
            new SetStudioViewTool(jobService, NullLogger<SetStudioViewTool>.Instance),
            new AddStudioWidgetTool(jobService, NullLogger<AddStudioWidgetTool>.Instance),
            new RemoveStudioWidgetTool(jobService, NullLogger<RemoveStudioWidgetTool>.Instance),
            new BindStudioInteractionTool(jobService, NullLogger<BindStudioInteractionTool>.Instance),
            new RemoveStudioInteractionTool(jobService, NullLogger<RemoveStudioInteractionTool>.Instance),
            new AddStudioControlTool(jobService, NullLogger<AddStudioControlTool>.Instance),
            new RemoveStudioControlTool(jobService, NullLogger<RemoveStudioControlTool>.Instance),
            new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance)
        ];
    }

}
