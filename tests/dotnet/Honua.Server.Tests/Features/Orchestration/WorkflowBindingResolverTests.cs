// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class WorkflowBindingResolverTests
{
    private static readonly RasterSecurityContextReference SecurityContext = new()
    {
        TenantId = "tenant:test",
        AuthorizationSnapshotReference = "workflow:test:submitter"
    };

    [UnitTest]
    public void Resolve_UnavailableStagedArtifact_FailsBeforeSubmittingDependentStep()
    {
        var step = new WorkflowStepDefinition
        {
            StepId = "dependent",
            Plan = new AnalysisPlan
            {
                PlanId = "plan-dependent",
                IntentId = "intent-dependent",
                Steps = []
            },
            InputBindings =
            [
                new StepInputBinding
                {
                    SourceStepId = "producer",
                    SourceArtifactSelector = "artifact:0",
                    TargetInputKey = "source"
                }
            ]
        };
        var artifact = new ArtifactRef
        {
            ArtifactId = "job-1:artifact:1",
            Kind = ArtifactKind.Raster,
            Label = "output1",
            Uri = null,
            Metadata = new Dictionary<string, string>
            {
                [RasterOutputArtifactMetadata.Staged] = "true"
            }
        };
        var upstream = new Dictionary<string, WorkflowStepState>
        {
            ["producer"] = new WorkflowStepState
            {
                StepId = "producer",
                PlanId = "plan-producer",
                Status = WorkflowStepStatus.Succeeded,
                OutputArtifacts = [artifact]
            }
        };

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);

        result.ResolvedValues.Should().BeEmpty();
        result.ResolvedRasterSources.Should().BeEmpty();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("content is unavailable on this host");
    }

    [UnitTest]
    public void Resolve_AvailableStagedRaster_BindsTypedWorkerSourceInsteadOfHttpRoute()
    {
        var step = BoundStep();
        var metadata = new Dictionary<string, string>
        {
            [RasterOutputArtifactMetadata.Staged] = "true",
            [RasterOutputArtifactMetadata.StoreProvider] = "Local",
            [RasterOutputArtifactMetadata.StoreReference] = "gp-outputs",
            [RasterOutputArtifactMetadata.ObjectKey] = "gp/outputs/job-1/a1/output/result.tif",
            [RasterOutputArtifactMetadata.SizeBytes] = "4096",
            [RasterOutputArtifactMetadata.MediaType] = "image/tiff",
            [RasterOutputArtifactMetadata.Checksum] = "sha256:" + new string('a', 64),
            [RasterOutputArtifactMetadata.GridWidth] = "32",
            [RasterOutputArtifactMetadata.GridHeight] = "16",
            [RasterOutputArtifactMetadata.GridBandCount] = "1",
            [RasterOutputArtifactMetadata.GridBitsPerSample] = "16",
            [RasterOutputArtifactMetadata.GridPixelScaleX] = "2.5",
            [RasterOutputArtifactMetadata.GridPixelScaleY] = "3.5",
        };
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:1",
            Kind = ArtifactKind.Raster,
            Label = "output1",
            Uri = "/api/geoprocessing/jobs/job-1/results/artifacts/0/content",
            ContentType = "image/tiff",
            Metadata = metadata,
        });

        var result = WorkflowBindingResolver.Resolve(step, upstream, SecurityContext);

        result.Failures.Should().BeEmpty();
        result.ResolvedValues.Should().BeEmpty();
        var staged = result.ResolvedRasterSources["source"]
            .Should().BeOfType<StagedArtifactRasterSourceDescriptor>().Subject;
        staged.ArtifactReference.Should().Be("job-1:artifact:1");
        staged.StoreReference.Should().Be("gp-outputs");
        staged.ObjectKey.Should().Be("gp/outputs/job-1/a1/output/result.tif");
        staged.Content.Checksum!.Value.Should().Be(new string('a', 64));
        staged.DeclaredDimensions.Should().Be(new RasterSourceDimensions(32, 16, 1, 16));
        staged.DeclaredPixelScale.Should().Be(new RasterSourcePixelScale(2.5, 3.5));

        var plan = new AnalysisPlan
        {
            PlanId = "plan",
            IntentId = "intent",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "native",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "raster.statistics",
                    Inputs = new Dictionary<string, string> { ["source"] = "legacy" }
                }
            ]
        };
        var bound = WorkflowBindingResolver.ApplyBindings(plan, result);
        bound.Steps[0].Inputs.Should().NotContainKey("source");
        bound.Steps[0].RasterSources["source"].Should().BeSameAs(staged);
    }

    [UnitTest]
    public void Resolve_AvailableStagedRasterWithoutBoundedGrid_FailsClosed()
    {
        var upstream = Upstream(new ArtifactRef
        {
            ArtifactId = "job-1:artifact:1",
            Kind = ArtifactKind.Raster,
            Label = "output1",
            Uri = "/api/geoprocessing/jobs/job-1/results/artifacts/0/content",
            ContentType = "image/tiff",
            Metadata = new Dictionary<string, string>
            {
                [RasterOutputArtifactMetadata.Staged] = "true",
                [RasterOutputArtifactMetadata.StoreProvider] = "Local",
                [RasterOutputArtifactMetadata.StoreReference] = "gp-outputs",
                [RasterOutputArtifactMetadata.ObjectKey] = "gp/outputs/job-1/a1/output/result.tif",
                [RasterOutputArtifactMetadata.SizeBytes] = "4096",
                [RasterOutputArtifactMetadata.MediaType] = "image/tiff",
                [RasterOutputArtifactMetadata.Checksum] = "sha256:" + new string('a', 64),
            },
        });

        var result = WorkflowBindingResolver.Resolve(BoundStep(), upstream, SecurityContext);

        result.ResolvedRasterSources.Should().BeEmpty();
        result.Failures.Should().ContainSingle().Which.Should().Contain("content identity is incomplete");
    }

    private static WorkflowStepDefinition BoundStep() => new()
    {
        StepId = "dependent",
        Plan = new AnalysisPlan { PlanId = "plan-dependent", IntentId = "intent-dependent", Steps = [] },
        InputBindings =
        [
            new StepInputBinding
            {
                SourceStepId = "producer",
                SourceArtifactSelector = "artifact:0",
                TargetInputKey = "source"
            }
        ]
    };

    private static Dictionary<string, WorkflowStepState> Upstream(ArtifactRef artifact)
        => new Dictionary<string, WorkflowStepState>
        {
            ["producer"] = new WorkflowStepState
            {
                StepId = "producer",
                PlanId = "plan-producer",
                Status = WorkflowStepStatus.Succeeded,
                OutputArtifacts = [artifact]
            }
        };
}
