using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IOperatorScopeAuthorizer
{
    OperatorScopeDecision Evaluate(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation);
}
