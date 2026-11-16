using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IOperatorApprovalEvaluator
{
    ApprovalRequirement Evaluate(ClaimsPrincipal principal, OperatorAuthorizationRequest request);
}
