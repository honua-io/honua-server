// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Geoprocessing;
using Honua.PackageReview;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.PackageReview;

[Protocol(TestProtocols.Admin)]
public sealed class AnalysisPlanPackageReviewAdapterTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ReviewAsync_WhenValidatePlanThrowsValidationException_ReturnsInvalidPayloadFinding()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.ValidatePlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Throws(new GeoprocessingValidationException("Plan step dependency graph contains a cycle."));
        var adapter = new AnalysisPlanPackageReviewAdapter(jobService);

        var result = await adapter.ReviewAsync(CreateRequest(), CreateContext(), CancellationToken.None);

        result.PreviewPlan.Should().BeNull();
        result.Findings.Should().ContainSingle(f =>
            f.Code == "invalid_analysis_plan_payload" &&
            f.Evidence.Any(e => e.Actual != null && e.Actual.Contains("contains a cycle", StringComparison.Ordinal)));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ReviewAsync_WhenDryRunPlanThrowsValidationException_ReturnsInvalidPayloadFindingWithoutPreview()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.ValidatePlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Returns(new PlanValidationResult { IsExecutable = true });
        jobService.DryRunPlan(Arg.Any<AnalysisPlan>(), Arg.Any<ClaimsPrincipal>())
            .Throws(new GeoprocessingValidationException("Plan failed catalog validation: UNKNOWN_PROCESS."));
        var adapter = new AnalysisPlanPackageReviewAdapter(jobService);

        var result = await adapter.ReviewAsync(CreateRequest(includePreview: true), CreateContext(), CancellationToken.None);

        result.PreviewPlan.Should().BeNull();
        result.Estimate.Should().BeNull();
        result.Findings.Should().ContainSingle(f =>
            f.Code == "invalid_analysis_plan_payload" &&
            f.Evidence.Any(e => e.Actual != null && e.Actual.Contains("UNKNOWN_PROCESS", StringComparison.Ordinal)));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task ReviewAsync_WithRoleContext_PreservesRoleClaimsForApprovalEvaluation()
    {
        ClaimsPrincipal? capturedPrincipal = null;
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.ValidatePlan(Arg.Any<AnalysisPlan>(), Arg.Do<ClaimsPrincipal>(p => capturedPrincipal = p))
            .Returns(new PlanValidationResult { IsExecutable = true });
        var adapter = new AnalysisPlanPackageReviewAdapter(jobService);
        var context = new PackageReviewContext
        {
            ActorId = "admin",
            SubjectId = "subject-admin",
            TenantId = "tenant-a",
            Scopes = ["packages.review"],
            Roles = ["admin"]
        };

        await adapter.ReviewAsync(CreateRequest(), context, CancellationToken.None);

        capturedPrincipal.Should().NotBeNull();
        capturedPrincipal!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("subject-admin");
        capturedPrincipal.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        capturedPrincipal.Claims.Should().Contain(c => c.Type == "roles" && c.Value == "admin");
        capturedPrincipal.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == "tenant-a");
    }

    private static PackageReviewRequest CreateRequest(bool includePreview = false)
    {
        using var document = JsonDocument.Parse(
            """
            {
              "planId": "plan-1",
              "intentId": "intent-1",
              "steps": [
                {
                  "stepId": "step-1",
                  "kind": "Geoprocess",
                  "processId": "geometry.buffer"
                }
              ],
              "outputs": [ "FeatureLayer" ]
            }
            """);

        return new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.AnalysisPlan,
            IncludePreviewPlan = includePreview,
            PackagePayload = document.RootElement.Clone()
        };
    }

    private static PackageReviewContext CreateContext()
        => new()
        {
            ActorId = "tester",
            Scopes = ["packages.review"]
        };
}
