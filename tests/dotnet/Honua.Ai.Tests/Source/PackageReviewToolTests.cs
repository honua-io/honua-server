// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Geoprocessing;
using Honua.PackageReview;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
public sealed class PackageReviewToolTests
{
    private readonly IPackageReviewService _reviewService = Substitute.For<IPackageReviewService>();
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_validate_package")]
    public async Task ValidatePackage_ReturnsCanonicalPackageReviewResponse()
    {
        _reviewService.ReviewAsync(
                Arg.Is<PackageReviewRequest>(r => !r.IncludePreviewPlan),
                Arg.Any<PackageReviewContext>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateResponse(includePreview: false));

        var tool = new ValidatePackageTool(
            _reviewService,
            _jobService,
            NullLogger<ValidatePackageTool>.Instance);
        var arguments = McpTestFactory.ToArguments(CreateRequest(), PackageReviewJsonContext.Default.PackageReviewRequest);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("contractVersion").GetString().Should().Be(PackageReviewContract.Version);
        structured.GetProperty("status").GetString().Should().Be(PackageReviewStatus.Blocked);
        structured.GetProperty("findings")[0].GetProperty("code").GetString().Should().Be("missing_data_binding");
        _jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.Process,
            OperatorOperation.Read);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_preview_package")]
    public async Task PreviewPackage_ForcesPreviewPlanAndReturnsSameShape()
    {
        _reviewService.ReviewAsync(
                Arg.Is<PackageReviewRequest>(r => r.IncludePreviewPlan),
                Arg.Any<PackageReviewContext>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateResponse(includePreview: true));

        var tool = new PreviewPackageTool(
            _reviewService,
            _jobService,
            NullLogger<PreviewPackageTool>.Instance);
        var arguments = McpTestFactory.ToArguments(CreateRequest(), PackageReviewJsonContext.Default.PackageReviewRequest);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("contractVersion").GetString().Should().Be(PackageReviewContract.Version);
        structured.GetProperty("previewPlan").GetProperty("mayMutatePublishedState").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_preview_package")]
    public async Task PreviewPackage_PreservesRoleClaimsInReviewContext()
    {
        PackageReviewContext? capturedContext = null;
        _reviewService.ReviewAsync(
                Arg.Any<PackageReviewRequest>(),
                Arg.Do<PackageReviewContext>(context => capturedContext = context),
                Arg.Any<CancellationToken>())
            .Returns(CreateResponse(includePreview: true));

        var tool = new PreviewPackageTool(
            _reviewService,
            _jobService,
            NullLogger<PreviewPackageTool>.Instance);
        var arguments = McpTestFactory.ToArguments(CreateRequest(), PackageReviewJsonContext.Default.PackageReviewRequest);
        var httpContext = McpTestFactory.AuthenticatedHttpContext("admin");
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "admin")],
            "Test"));

        var result = await tool.InvokeAsync(httpContext, arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        capturedContext.Should().NotBeNull();
        capturedContext!.Roles.Should().Contain("admin");
    }

    private static PackageReviewRequest CreateRequest()
        => new()
        {
            PackageFamily = PackageReviewFamilies.Query,
            PackageId = "pkg-mcp"
        };

    private static PackageReviewResponse CreateResponse(bool includePreview)
        => new()
        {
            ContractVersion = PackageReviewContract.Version,
            ReviewId = "prv_test",
            PackageFamily = PackageReviewFamilies.Query,
            PackageId = "pkg-mcp",
            Status = PackageReviewStatus.Blocked,
            CanExecute = false,
            CanPublish = false,
            CheckedAt = DateTimeOffset.UnixEpoch,
            Findings =
            [
                new PackageFinding
                {
                    Code = "missing_data_binding",
                    Severity = PackageFindingSeverity.Blocker,
                    Category = PackageFindingCategory.DataBinding,
                    Message = "Missing data binding."
                }
            ],
            PreviewPlan = includePreview
                ? new PackagePreviewPlan
                {
                    PlanId = "preview:pkg-mcp",
                    MayMutatePublishedState = false,
                    Operations =
                    [
                        new PackagePreviewOperation
                        {
                            OperationId = "review",
                            OperationKind = "query",
                            MayMutatePublishedState = false
                        }
                    ]
                }
                : null
        };
}
