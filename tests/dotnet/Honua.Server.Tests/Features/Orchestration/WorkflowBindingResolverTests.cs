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

        var result = WorkflowBindingResolver.Resolve(step, upstream);

        result.ResolvedValues.Should().BeEmpty();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("content is unavailable on this host");
    }
}
