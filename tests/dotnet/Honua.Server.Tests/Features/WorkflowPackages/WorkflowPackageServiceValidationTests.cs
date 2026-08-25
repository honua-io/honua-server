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
                WorkflowPublicationTarget.Schedule)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkflowPublicationTarget.Job)]
    [InlineData(WorkflowPublicationTarget.ProcessEndpoint)]
    public void IsDirectSubmitOnlyValidation_WorkflowOnlyDirectTarget_RetainsFailure(
        WorkflowPublicationTarget target)
    {
        WorkflowPackageService.IsDirectSubmitOnlyValidation(WorkflowOnlyFailure, target)
            .Should().BeFalse();
    }
}
