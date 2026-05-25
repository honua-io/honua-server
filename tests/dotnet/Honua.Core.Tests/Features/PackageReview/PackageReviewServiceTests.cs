// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.PackageReview;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.PackageReview;

public sealed class PackageReviewServiceTests
{
    [Fact]
    public async Task ReviewAsync_WithResolvedRequirements_ReturnsReadyAndStableReviewId()
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.Query,
            PackageId = "pkg-ready",
            IncludePassFindings = true,
            Requirements = new PackageReviewRequirements
            {
                DataBindings =
                [
                    new PackageDataBindingRequirement
                    {
                        Id = "cities",
                        SourceId = "layers/cities",
                        IsResolved = true
                    }
                ]
            }
        };

        var first = await service.ReviewAsync(request, CreateContext());
        var second = await service.ReviewAsync(request, CreateContext());

        first.Status.Should().Be(PackageReviewStatus.Ready);
        first.CanExecute.Should().BeTrue();
        first.CanPublish.Should().BeTrue();
        first.ReviewId.Should().Be(second.ReviewId);
        first.Findings.Should().Contain(f => f.Severity == PackageFindingSeverity.Pass);
    }

    [Fact]
    public async Task ReviewAsync_WithWarningOnly_ReturnsWarningWithoutBlockingActions()
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.MapPackage,
            PackageId = "pkg-warning",
            Requirements = new PackageReviewRequirements
            {
                Crs =
                [
                    new PackageCrsRequirement
                    {
                        ActualSrid = 4326,
                        Assumption = "Input datum is assumed to be WGS84."
                    }
                ]
            }
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.Status.Should().Be(PackageReviewStatus.Warning);
        response.CanExecute.Should().BeTrue();
        response.CanPublish.Should().BeTrue();
        response.Findings.Should().ContainSingle(f => f.Code == "crs_assumption_requires_review");
    }

    [Fact]
    public async Task ReviewAsync_WithBlockers_ReturnsBlockedAndActionableFindingsFirst()
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.AppPackage,
            PackageId = "pkg-blocked",
            Requirements = new PackageReviewRequirements
            {
                DataBindings =
                [
                    new PackageDataBindingRequirement
                    {
                        Id = "source",
                        SourceId = "missing-layer",
                        IsResolved = false,
                        Path = "$.sources[0]"
                    }
                ],
                Permissions =
                [
                    new PackagePermissionRequirement
                    {
                        Permission = "catalog.publish",
                        Granted = false,
                        PolicyRef = "policy/catalog-publish",
                        AppliesTo = PackageReviewActionScope.Publish
                    }
                ],
                Fields =
                [
                    new PackageFieldRequirement
                    {
                        Name = "status",
                        Exists = false,
                        ExpectedType = "string",
                        Path = "$.widgets[0].field"
                    }
                ],
                Crs =
                [
                    new PackageCrsRequirement
                    {
                        ExpectedSrid = 4326,
                        ActualSrid = 3857,
                        ExpectedUnits = "degree",
                        ActualUnits = "meter"
                    }
                ],
                Capabilities =
                [
                    new PackageCapabilityRequirement
                    {
                        Capability = "dashboard.realtime",
                        Supported = false,
                        AppliesTo = PackageReviewActionScope.Execute
                    }
                ],
                Approvals =
                [
                    new PackageApprovalRequirement
                    {
                        Required = true,
                        Approved = false,
                        PolicyRef = "approval/app-publish",
                        AppliesTo = PackageReviewActionScope.Publish
                    }
                ]
            }
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.Status.Should().Be(PackageReviewStatus.Blocked);
        response.CanExecute.Should().BeFalse();
        response.CanPublish.Should().BeFalse();
        response.RequiresApproval.Should().BeTrue();
        response.Findings.Take(7).Should().OnlyContain(f => f.Severity == PackageFindingSeverity.Blocker);
        response.Findings.Select(f => f.Code).Should().Contain(new[]
        {
            "missing_data_binding",
            "permission_denied",
            "missing_field",
            "crs_srid_mismatch",
            "crs_units_mismatch",
            "unsupported_capability",
            "approval_required"
        });
        response.Findings.Should().OnlyContain(f => f.RequiredAction != null);
    }

    [Fact]
    public async Task ReviewAsync_WithPublishOnlyBlocker_AllowsExecute()
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.Query,
            Requirements = new PackageReviewRequirements
            {
                Permissions =
                [
                    new PackagePermissionRequirement
                    {
                        Permission = "catalog.publish",
                        Granted = false,
                        AppliesTo = PackageReviewActionScope.Publish
                    }
                ]
            }
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.Status.Should().Be(PackageReviewStatus.Blocked);
        response.CanExecute.Should().BeTrue();
        response.CanPublish.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewAsync_WithPreviewRequest_ReturnsReadOnlyPreviewPlan()
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.Query,
            PackageId = "query-preview",
            IncludePreviewPlan = true,
            Requirements = new PackageReviewRequirements
            {
                DataBindings =
                [
                    new PackageDataBindingRequirement
                    {
                        Id = "parks",
                        SourceId = "layers/parks",
                        IsResolved = true
                    }
                ],
                Capabilities =
                [
                    new PackageCapabilityRequirement
                    {
                        Capability = "features.query",
                        Supported = true
                    }
                ]
            }
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.PreviewPlan.Should().NotBeNull();
        response.PreviewPlan!.MayMutatePublishedState.Should().BeFalse();
        response.PreviewPlan.Operations.Should().NotBeEmpty();
        response.PreviewPlan.Operations.Should().OnlyContain(o => !o.MayMutatePublishedState);
        response.PreviewPlan.Operations[0].InputRefs.Should().Contain("layers/parks");
    }

    [Theory]
    [InlineData(PackageReviewFamilies.AnalysisPlan)]
    [InlineData(PackageReviewFamilies.Geoprocessing)]
    public async Task ReviewAsync_WithSpecializedPreviewFamily_DoesNotReturnGenericPreviewPlan(string family)
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = family,
            PackageId = "specialized-preview",
            IncludePreviewPlan = true,
            Requirements = new PackageReviewRequirements
            {
                DataBindings =
                [
                    new PackageDataBindingRequirement
                    {
                        Id = "source",
                        SourceId = "layers/source",
                        IsResolved = true
                    }
                ]
            }
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.PreviewPlan.Should().BeNull();
    }

    [Fact]
    public async Task ReviewAsync_WithUnsupportedFamily_ReturnsUnsupportedBlocker()
    {
        var service = CreateService();
        var request = new PackageReviewRequest
        {
            PackageFamily = "unsupported_family"
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.Status.Should().Be(PackageReviewStatus.Blocked);
        response.Findings.Should().ContainSingle(f =>
            f.Code == "unsupported_package_family" &&
            f.Disposition == PackageFindingDisposition.Unsupported);
    }

    [Fact]
    public async Task ReviewAsync_WithExpensiveEstimate_ReturnsWarningAndEchoesEstimate()
    {
        var service = CreateService();
        var estimate = new PackageEstimate
        {
            RowCount = 1_500_000,
            DurationMs = 45_000,
            CostWeight = 1500,
            Confidence = "high"
        };
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.Etl,
            Estimate = estimate
        };

        var response = await service.ReviewAsync(request, CreateContext());

        response.Status.Should().Be(PackageReviewStatus.Warning);
        response.Estimate.Should().BeSameAs(estimate);
        response.Findings.Should().ContainSingle(f => f.Code == "expensive_preview_estimate");
    }

    [Fact]
    public async Task ReviewAsync_WithExplicitlyNullRequirementsAndResourceRefs_ReturnsDeterministicResponse()
    {
        // Loosely typed clients can send "requirements": null and
        // "resourceRefs": null. The request model normalizes both to empty so
        // the service returns a deterministic review instead of an internal
        // error.
        var request = JsonSerializer.Deserialize(
            """{ "packageFamily": "query", "requirements": null, "resourceRefs": null }""",
            PackageReviewCoreJsonContext.Default.PackageReviewRequest)!;

        request.Requirements.Should().NotBeNull();
        request.ResourceRefs.Should().NotBeNull();

        var service = CreateService();
        var response = await service.ReviewAsync(request, CreateContext());

        response.Status.Should().Be(PackageReviewStatus.Ready);
        response.PackageFamily.Should().Be(PackageReviewFamilies.Query);
        response.ReviewId.Should().NotBeNullOrEmpty();
    }

    private static IPackageReviewService CreateService()
    {
        var services = new ServiceCollection();
        services.AddPackageReviewCore();
        return services.BuildServiceProvider().GetRequiredService<IPackageReviewService>();
    }

    private static PackageReviewContext CreateContext()
        => new()
        {
            TenantId = "tenant-a",
            ActorId = "tester",
            Scopes = ["admin"]
        };
}
