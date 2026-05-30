using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Tests.Features.Authorization;

internal static class OperatorAuthorizationEvaluatorTestExtensions
{
    public static AccessDecision Evaluate(
        this OperatorAuthorizationEvaluator evaluator,
        ClaimsPrincipal principal,
        OperatorAuthorizationRequest request)
        => evaluator.EvaluateAsync(principal, request).GetAwaiter().GetResult();
}
