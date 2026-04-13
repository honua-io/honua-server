// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Scoped façade that groups the operator authorization and approval evaluators into
/// a single injection point, keeping endpoint parameter counts within the project's
/// 5-dependency limit.
/// </summary>
internal sealed class OperatorApprovalGate(
    IOperatorAuthorizationEvaluator authEvaluator,
    IOperatorApprovalEvaluator approvalEvaluator,
    ILogger<OperatorApprovalGate> logger)
{
    private const string ApprovalRequiredType = "urn:honua:approval-required";

    /// <summary>
    /// Evaluates operator authorization and returns a structured error result if denied; null if allowed.
    /// Uses ProblemDetails (RFC 9457) format for admin API surfaces.
    /// </summary>
    public IResult? EvaluateAuthorization(
        HttpContext context,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        string? resourceId = null,
        bool isDestructive = false)
    {
        var decision = CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation,
            ResourceId = resourceId,
            IsDestructive = isDestructive
        });

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

    /// <summary>
    /// Evaluates operator approval and returns a structured error result if approval is required; null if not.
    /// Uses ProblemDetails (RFC 9457) format with approval-specific extensions for admin API surfaces.
    /// </summary>
    public IResult? EvaluateApproval(
        HttpContext context,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        string? resourceId = null,
        bool isDestructive = false)
    {
        var request = new OperatorAuthorizationRequest
        {
            ResourceType = resourceType,
            Operation = operation,
            ResourceId = resourceId,
            IsDestructive = isDestructive
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

    /// <summary>
    /// Transport-neutral authorization check. Returns the raw <see cref="AccessDecision"/>
    /// for callers that need to format responses in protocol-specific formats (OGC, OData, etc.).
    /// </summary>
    public AccessDecision CheckAuthorization(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
    {
        var decision = authEvaluator.Evaluate(principal, request);
        EnrichActivity(request, decision.IsAllowed ? "allowed" : "denied");
        return decision;
    }

    /// <summary>
    /// Transport-neutral approval check. Returns the raw <see cref="ApprovalRequirement"/>
    /// for callers that need to format responses in protocol-specific formats or non-HTTP surfaces.
    /// </summary>
    public ApprovalRequirement CheckApproval(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
    {
        var approval = approvalEvaluator.Evaluate(principal, request);

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        var outcome = approval.IsRequired ? "required" : "not-required";

        OperatorAuthorizationLog.ApprovalGateEvaluated(
            logger, userId, request.ResourceType, request.Operation, outcome);

        EnrichActivity(request, $"approval-{outcome}");
        return approval;
    }

    private static void EnrichActivity(OperatorAuthorizationRequest request, string outcome)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag(HonuaTelemetry.Tags.Operation, $"{request.ResourceType}.{request.Operation}");
        activity.SetTag("honua.approval.outcome", outcome);
    }
}

/// <summary>
/// ProblemDetails response with approval-specific extension members (RFC 9457).
/// </summary>
internal sealed record ApprovalDeniedProblemDetails
{
    /// <summary>
    /// Stable, machine-readable problem type URI.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Short, human-readable summary.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// HTTP status code.
    /// </summary>
    public required int Status { get; init; }

    /// <summary>
    /// Human-readable explanation with policy reference.
    /// </summary>
    public required string Detail { get; init; }

    /// <summary>
    /// URI reference identifying the specific occurrence.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>
    /// Correlation identifier for tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// The triggering policy reference.
    /// </summary>
    public string? PolicyRef { get; init; }

    /// <summary>
    /// Machine-readable reason codes explaining why approval is required.
    /// </summary>
    public IReadOnlyList<string>? ReasonCodes { get; init; }

    /// <summary>
    /// The resource type that was gated.
    /// </summary>
    public string? ResourceType { get; init; }

    /// <summary>
    /// The operation that was gated.
    /// </summary>
    public string? Operation { get; init; }
}

/// <summary>
/// Source-generated JSON serialization context for approval gate responses.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApprovalDeniedProblemDetails))]
internal sealed partial class ApprovalGateJsonContext : JsonSerializerContext;
