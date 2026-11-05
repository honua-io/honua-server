// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Infrastructure.Authentication;

internal sealed class OperatorApprovalGate(
    IOperatorAuthorizationEvaluator authEvaluator,
    IOperatorApprovalEvaluator approvalEvaluator,
    ILogger<OperatorApprovalGate> logger)
{
    private const string ApprovalRequiredType = "urn:honua:approval-required";

    public async Task<IResult?> EvaluateAuthorizationAsync(
        HttpContext context,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        string? resourceId = null,
        bool isDestructive = false)
    {
        var decision = await CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                Operation = operation,
                ResourceId = resourceId,
                IsDestructive = isDestructive
            },
            context.RequestAborted).ConfigureAwait(false);

        if (decision.IsAllowed) return null;

        if (decision.RequiresAuthentication)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status401Unauthorized,
                "Authentication is required for this operation.");
        }

        return ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status403Forbidden,
            "You do not have permission to perform this operation.");
    }

    public IResult? EvaluateApproval(
        HttpContext context,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        string? resourceId = null,
        bool isDestructive = false,
        bool requiresExplicitApproval = false,
        string? approvalPolicyRef = null,
        IReadOnlyList<string>? approvalReasonCodes = null)
    {
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation,
            ResourceId = resourceId,
            IsDestructive = isDestructive,
            RequiresExplicitApproval = requiresExplicitApproval,
            ApprovalPolicyRef = approvalPolicyRef,
            ApprovalReasonCodes = approvalReasonCodes ?? Array.Empty<string>()
        };

        var approval = CheckApproval(context.User, request);
        if (!approval.IsRequired) return null;

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? context.User.FindFirstValue("sub");

        OperatorAuthorizationLog.ApprovalGateDenied(
            logger, userId, resourceType, operation, approval.PolicyRef);

        if (isDestructive)
        {
            OperatorAuthorizationLog.DestructiveActionGated(
                logger, userId, resourceType, operation, approval.PolicyRef);
        }

        return Results.Json(
            new ApprovalDeniedProblemDetails
            {
                Type = ApprovalRequiredType,
                Title = "Approval required",
                Status = StatusCodes.Status403Forbidden,
                Detail = $"This operation requires approval (policy: {approval.PolicyRef}).",
                Instance = context.Request.Path.Value,
                CorrelationId = context.TraceIdentifier,
                PolicyRef = approval.PolicyRef,
                ReasonCodes = approval.ReasonCodes,
                ResourceType = resourceType.ToString(),
                Operation = operation.ToString()
            },
            ApprovalGateJsonContext.Default.ApprovalDeniedProblemDetails,
            statusCode: StatusCodes.Status403Forbidden,
            contentType: ProblemDetailsHelpers.ContentType);
    }

    public async Task<AccessDecision> CheckAuthorizationAsync(
        ClaimsPrincipal principal,
        OperatorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var decision = await authEvaluator.EvaluateAsync(principal, request, cancellationToken).ConfigureAwait(false);
        EnrichAuthorizationActivity(request, decision.IsAllowed ? "allowed" : "denied");
        return decision;
    }

    public ApprovalRequirement CheckApproval(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
    {
        var approval = approvalEvaluator.Evaluate(principal, request);

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        var outcome = approval.IsRequired ? "required" : "not-required";

        OperatorAuthorizationLog.ApprovalGateEvaluated(
            logger, userId, request.ResourceType, request.Operation, outcome);

        EnrichApprovalActivity(request, outcome);
        return approval;
    }

    private static void EnrichAuthorizationActivity(OperatorAuthorizationRequest request, string outcome)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag("honua.gate.resource_type", request.ResourceType.ToString());
        activity.SetTag("honua.gate.operation", request.Operation.ToString());
        activity.SetTag("honua.authorization.outcome", outcome);
    }

    private static void EnrichApprovalActivity(OperatorAuthorizationRequest request, string outcome)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag("honua.gate.resource_type", request.ResourceType.ToString());
        activity.SetTag("honua.gate.operation", request.Operation.ToString());
        activity.SetTag("honua.approval.outcome", outcome);
    }
}

internal sealed record ApprovalDeniedProblemDetails
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public required string Detail { get; init; }

    public string? Instance { get; init; }

    public string? CorrelationId { get; init; }

    public string? PolicyRef { get; init; }

    public IReadOnlyList<string>? ReasonCodes { get; init; }

    public string? ResourceType { get; init; }

    public string? Operation { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApprovalDeniedProblemDetails))]
internal sealed partial class ApprovalGateJsonContext : JsonSerializerContext;
