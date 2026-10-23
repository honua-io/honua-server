// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.WorkflowPackages;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed class WorkflowPackageServiceValidationTests
{
    private static readonly GeoprocessingValidationFailure WorkflowOnlyFailure = new()
    {
        Code = "WORKFLOW_ONLY_PROCESS",
        Message = "Workflow-only process.",
        FieldPath = "steps[0].processId"
    };

    [Fact]
    public void IsDirectSubmitOnlyValidation_WorkflowOnlySchedule_SuppressesFailure()
    {
        WorkflowPackageService.IsDirectSubmitOnlyValidation(
                WorkflowOnlyFailure,
                WorkflowPublicationTarget.Schedule,
                orchestrationAvailable: true)
            .Should().BeTrue();
    }

    [Fact]
    public void IsDirectSubmitOnlyValidation_WorkflowOnlyScheduleWithoutOrchestration_RetainsFailure()
    {
        WorkflowPackageService.IsDirectSubmitOnlyValidation(
                WorkflowOnlyFailure,
                WorkflowPublicationTarget.Schedule,
                orchestrationAvailable: false)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkflowPublicationTarget.Job)]
    [InlineData(WorkflowPublicationTarget.ProcessEndpoint)]
    public void IsDirectSubmitOnlyValidation_WorkflowOnlyDirectTarget_RetainsFailure(
        WorkflowPublicationTarget target)
    {
        WorkflowPackageService.IsDirectSubmitOnlyValidation(
                WorkflowOnlyFailure,
                target,
                orchestrationAvailable: true)
            .Should().BeFalse();
    }

    [Fact]
    public void IsDirectSubmitOnlyValidation_WorkflowOnlyPackageValidation_SuppressesFailure()
    {
        WorkflowPackageService.IsDirectSubmitOnlyValidation(
                WorkflowOnlyFailure,
                target: null,
                orchestrationAvailable: false)
            .Should().BeTrue();
    }

    [Fact]
    public void BuildRunProvenance_RunParameters_AreCarriedUnderTheRunParameterNamespace()
    {
        var publicationProvenance = new Dictionary<string, string>
        {
            [WorkflowPackageMetadataKeys.PackageId] = "pkg-1",
            [WorkflowPackageMetadataKeys.PackageHash] = "hash-1"
        };

        var provenance = WorkflowPackageService.BuildRunProvenance(
            publicationProvenance,
            new Dictionary<string, string>
            {
                ["analysis.region"] = "pacific",
                ["process.executable"] = "request-value",
                ["env.SAMPLE_SETTING"] = "request-value",
                ["batch.job_queue_arn"] = "request-value",
                [WorkflowPackageMetadataKeys.PackageId] = "other-package",
                [" "] = "ignored"
            });

        // Run parameters stay traceable on the run, under their own namespace.
        provenance.Should().Contain("workflow.parameter.analysis.region", "pacific");
        provenance.Should().Contain("workflow.parameter.process.executable", "request-value");
        provenance.Should().Contain("workflow.parameter.env.SAMPLE_SETTING", "request-value");
        provenance.Should().Contain("workflow.parameter.batch.job_queue_arn", "request-value");

        // No run parameter is carried under its bare name, and stamped provenance is unchanged.
        provenance.Keys.Should().OnlyContain(key => key.StartsWith("workflow.", StringComparison.Ordinal));
        provenance.Should().Contain(WorkflowPackageMetadataKeys.PackageId, "pkg-1");
        provenance.Should().Contain(WorkflowPackageMetadataKeys.PackageHash, "hash-1");
        provenance.Should().HaveCount(6);
    }

    [Fact]
    public void BuildRunProvenance_WithoutRunParameters_ReturnsPublicationProvenance()
    {
        var publicationProvenance = new Dictionary<string, string>
        {
            [WorkflowPackageMetadataKeys.PackageId] = "pkg-1"
        };

        WorkflowPackageService.BuildRunProvenance(publicationProvenance, requestParameters: null)
            .Should().BeEquivalentTo(publicationProvenance);
    }
}
