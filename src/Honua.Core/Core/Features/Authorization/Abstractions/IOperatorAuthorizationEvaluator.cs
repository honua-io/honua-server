using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

public interface IOperatorAuthorizationEvaluator
{
    Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request, CancellationToken cancellationToken = default);
}
