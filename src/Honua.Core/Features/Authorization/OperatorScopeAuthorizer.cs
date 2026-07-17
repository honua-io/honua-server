using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

/// <summary>
/// Default <see cref="IOperatorScopeAuthorizer"/>. Reads the OAuth scope claims a bearer
/// token carries and intersects them, through <see cref="OperatorScopeCatalog"/>, with the
/// requested operator operation (honua-server#2851).
/// </summary>
/// <remarks>
/// The intersection can only ever narrow the grant model. A principal that is not
/// scope-governed — an X-API-Key caller, an interactive session, the dev-auth bypass — is
/// returned <see cref="OperatorScopeDecision.NotGoverned"/> so its grant decision stands
/// unchanged. A scope-governed principal (a validated bearer token) whose scopes do not cover
/// the operation is denied, and a bearer token that carries no recognized scope at all is
/// fail-closed: it can perform nothing until its issuer stamps an explicit Honua MCP scope.
/// </remarks>
public sealed class OperatorScopeAuthorizer : IOperatorScopeAuthorizer
{
    /// <inheritdoc />
    public OperatorScopeDecision Evaluate(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!OperatorScopeCatalog.IsScopeGoverned(principal))
        {
            return OperatorScopeDecision.NotGoverned();
        }

        var recognized = OperatorScopeCatalog.CollectRecognizedScopes(principal);
        if (recognized.Count == 0)
        {
            // Fail-closed default: a bearer token that presents no recognized Honua MCP scope
            // authorizes nothing, even when its principal's grants (or admin role) would allow
            // the operation. Least-privilege delegation is the entire reason OAuth scopes exist
            // on this surface.
            return OperatorScopeDecision.Denied(
                $"The access token carries no recognized Honua MCP scope, so '{operation}' on {resourceType} is denied. "
                + "Request a token whose scope claim includes the scope for this operation.");
        }

        if (OperatorScopeCatalog.PermitsOperation(recognized, operation))
        {
            return OperatorScopeDecision.Allowed();
        }

        return OperatorScopeDecision.Denied(
            $"The access token's scopes do not permit '{operation}' on {resourceType}. "
            + "Request a token whose scope claim includes the scope for this operation.");
    }
}

/// <summary>
/// No-op <see cref="IOperatorScopeAuthorizer"/> that never narrows a grant. Used where an
/// operator authorizer is constructed without OAuth scope governance (direct unit-test
/// construction and legacy collaborator wiring), preserving the pre-scope behaviour exactly.
/// </summary>
public sealed class NullOperatorScopeAuthorizer : IOperatorScopeAuthorizer
{
    /// <summary>Shared singleton instance.</summary>
    public static NullOperatorScopeAuthorizer Instance { get; } = new();

    private NullOperatorScopeAuthorizer()
    {
    }

    /// <inheritdoc />
    public OperatorScopeDecision Evaluate(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
        => OperatorScopeDecision.NotGoverned();
}
