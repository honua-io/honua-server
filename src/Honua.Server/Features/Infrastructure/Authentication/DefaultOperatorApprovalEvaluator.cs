using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Default approval evaluator with enriched policy rules for operator actions.
/// </summary>
internal sealed class DefaultOperatorApprovalEvaluator(
    IOptions<OperatorApprovalOptions> options,
    IOptions<RbacOptions> rbacOptions,
    ILogger<DefaultOperatorApprovalEvaluator> logger) : IOperatorApprovalEvaluator
{
    public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");

        // Admin exemption: admins bypass approval gates when configured.
        if (options.Value.AdminExemptFromApproval && IsAdmin(principal))
        {
            OperatorAuthorizationLog.ApprovalNotRequired(
                logger, userId, request.ResourceType, request.Operation);
            return ApprovalRequirement.NotRequired();
        }

        if (request.Operation == OperatorOperation.Publish && options.Value.PublishRequiresApproval)
        {
            // Deploy-publish distinction: when publishing a deployment, emit an additional
            // reason code so deploy workflows can distinguish deploy-publish from package-publish.
            var reasons = request.ResourceType == OperatorResourceType.Deployment
                ? new[] { "publish-requires-approval", "deploy-publish" }
                : new[] { "publish-requires-approval" };

            var result = ApprovalRequirement.Required("operator.publish", reasons);
            OperatorAuthorizationLog.ApprovalRequired(
                logger, userId, request.ResourceType, request.Operation, result.PolicyRef);
            return result;
        }

        if (request.Operation == OperatorOperation.Promote
            && request.WorkspaceVisibility is WorkspaceVisibility.Public or WorkspaceVisibility.Organization)
        {
            var result = ApprovalRequirement.Required("operator.promote-wide", "promote-to-wide-visibility");
            OperatorAuthorizationLog.ApprovalRequired(
                logger, userId, request.ResourceType, request.Operation, result.PolicyRef);
            return result;
        }

        if (request.RequiresExplicitApproval)
        {
            var policyRef = string.IsNullOrWhiteSpace(request.ApprovalPolicyRef)
                ? $"operator.explicit.{request.ResourceType.ToString().ToLowerInvariant()}"
                : request.ApprovalPolicyRef;
            var reasonCodes = request.ApprovalReasonCodes.Count > 0
                ? request.ApprovalReasonCodes.ToArray()
                : ["explicit-approval-required"];
            var result = ApprovalRequirement.Required(policyRef, reasonCodes);
            OperatorAuthorizationLog.ApprovalRequired(
                logger, userId, request.ResourceType, request.Operation, result.PolicyRef);
            return result;
        }

        if (request.IsDestructive && options.Value.DestructiveActionsRequireApproval)
        {
            // Resource-qualified policy ref: include the resource family so downstream consumers
            // get inspectable reason codes per resource type.
            var policyRef = $"operator.destructive.{request.ResourceType.ToString().ToLowerInvariant()}";
            var result = ApprovalRequirement.Required(policyRef, "destructive-action-requires-approval");
            OperatorAuthorizationLog.ApprovalRequired(
                logger, userId, request.ResourceType, request.Operation, result.PolicyRef);
            return result;
        }

        OperatorAuthorizationLog.ApprovalNotRequired(
            logger, userId, request.ResourceType, request.Operation);
        return ApprovalRequirement.NotRequired();
    }

    private bool IsAdmin(ClaimsPrincipal principal)
    {
        var roleClaimType = rbacOptions.Value.EffectiveRoleClaimType;
        var checkStandardRoleClaim = !string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase);

        foreach (var claim in principal.Claims)
        {
            var isRoleClaim = string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase)
                || (checkStandardRoleClaim && string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase));

            if (!isRoleClaim || string.IsNullOrWhiteSpace(claim.Value))
                continue;

            if (string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
