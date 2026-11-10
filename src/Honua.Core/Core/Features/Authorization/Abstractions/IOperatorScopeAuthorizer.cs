using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Intersects an OAuth 2.1 bearer token's scopes with a requested operator operation
/// (honua-server#2851). Composes with <see cref="IOperatorAuthorizationEvaluator"/> as a
/// narrowing gate: the grant model decides what a principal <em>may</em> do, and this decides
/// whether the presented token's scopes still permit it. Scopes can only narrow, never widen.
/// </summary>
public interface IOperatorScopeAuthorizer
{
    /// <summary>
    /// Evaluates whether the principal's OAuth scopes permit the given resource/operation.
    /// Returns <see cref="OperatorScopeDecision.NotGoverned"/> for non-OAuth principals so the
    /// grant decision stands unchanged; for a scope-governed principal with no recognized scope
    /// the result is a fail-closed denial.
    /// </summary>
    OperatorScopeDecision Evaluate(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation);
}
