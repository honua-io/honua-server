// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.PackageReview;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal abstract class PackageReviewToolBase : IMcpTool
{
    private readonly IPackageReviewService _reviewService;
    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger _logger;

    protected PackageReviewToolBase(
        IPackageReviewService reviewService,
        IGeoprocessingJobService jobService,
        ILogger logger)
    {
        _reviewService = reviewService;
        _jobService = jobService;
        _logger = logger;
    }

    public abstract string Name { get; }

    protected abstract string OperationName { get; }

    protected abstract bool IncludePreviewPlan { get; }

    protected abstract string Description { get; }

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Planning;

    public McpToolDescriptor Describe() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = McpToolSchemas.PackageReviewArgumentSchema
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity(OperationName);
        McpLog.ToolInvoked(_logger, Name, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        _jobService.EnsureCallerAuthorized(principal, OperatorResourceType.Process, OperatorOperation.Read);

        var request = McpToolHelpers.ParseArguments(arguments, McpJsonContext.Default.PackageReviewRequest)
            .WithPreviewPlan(IncludePreviewPlan);
        var response = await _reviewService.ReviewAsync(
            request,
            PackageReviewContextFactory.FromHttpContext(httpContext),
            cancellationToken).ConfigureAwait(false);

        return McpToolHelpers.SuccessResult(response, McpJsonContext.Default.PackageReviewResponse);
    }
}

internal sealed class ValidatePackageTool : PackageReviewToolBase
{
    public const string ToolName = "honua_validate_package";

    public ValidatePackageTool(
        IPackageReviewService reviewService,
        IGeoprocessingJobService jobService,
        ILogger<ValidatePackageTool> logger)
        : base(reviewService, jobService, logger)
    {
    }

    public override string Name => ToolName;

    protected override string OperationName => "ValidatePackage";

    protected override bool IncludePreviewPlan => false;

    protected override string Description => "Validate any supported Honua package using the canonical package-review response shape.";
}

internal sealed class PreviewPackageTool : PackageReviewToolBase
{
    public const string ToolName = "honua_preview_package";

    public PreviewPackageTool(
        IPackageReviewService reviewService,
        IGeoprocessingJobService jobService,
        ILogger<PreviewPackageTool> logger)
        : base(reviewService, jobService, logger)
    {
    }

    public override string Name => ToolName;

    protected override string OperationName => "PreviewPackage";

    protected override bool IncludePreviewPlan => true;

    protected override string Description => "Validate a package and return a read-only preview plan using the canonical package-review response shape.";
}
