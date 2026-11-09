using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Transport-neutral evaluator for operator-scoped resource authorization.
/// Takes a <see cref="ClaimsPrincipal"/> directly — no HttpContext, ServerCallContext, or MCP session.
/// The transport layer extracts the principal and calls this.
/// </summary>
public interface IOperatorAuthorizationEvaluator
{
    /// <summary>
    /// Evaluates whether the given principal is authorized to perform the requested operation.
    /// </summary>
    Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request, CancellationToken cancellationToken = default);
}
