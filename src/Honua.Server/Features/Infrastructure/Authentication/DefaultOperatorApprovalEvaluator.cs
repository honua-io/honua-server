using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Default approval evaluator with baseline rules.
/// Placeholder for #726 to replace with richer approval logic.
/// </summary>
internal sealed class DefaultOperatorApprovalEvaluator(
    IOptions<OperatorApprovalOptions> options,
    ILogger<DefaultOperatorApprovalEvaluator> logger) : IOperatorApprovalEvaluator
{
    public ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");

        if (request.Operation == OperatorOperation.Publish && options.Value.PublishRequiresApproval)
        {
            var result = ApprovalRequirement.Required("operator.publish", "publish-requires-approval");
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

        if (request.IsDestructive && options.Value.DestructiveActionsRequireApproval)
        {
            var result = ApprovalRequirement.Required("operator.destructive", "destructive-action-requires-approval");
            OperatorAuthorizationLog.ApprovalRequired(
                logger, userId, request.ResourceType, request.Operation, result.PolicyRef);
            return result;
        }

        OperatorAuthorizationLog.ApprovalNotRequired(
            logger, userId, request.ResourceType, request.Operation);
        return ApprovalRequirement.NotRequired();
    }
}
