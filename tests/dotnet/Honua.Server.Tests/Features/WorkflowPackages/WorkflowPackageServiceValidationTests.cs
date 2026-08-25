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
}
