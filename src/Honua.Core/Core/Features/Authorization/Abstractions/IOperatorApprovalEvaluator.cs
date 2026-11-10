using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Evaluates whether an authorized action additionally requires human approval.
/// Only called after authorization succeeds. Feeds into PlanValidationResult.requiresApproval.
/// </summary>
public interface IOperatorApprovalEvaluator
{
    /// <summary>
    /// Determines whether the authorized action requires human sign-off before execution.
    /// </summary>
    ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request);
}
