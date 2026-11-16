using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

public sealed class OperatorScopeAuthorizer : IOperatorScopeAuthorizer
{
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

public sealed class NullOperatorScopeAuthorizer : IOperatorScopeAuthorizer
{
    public static NullOperatorScopeAuthorizer Instance { get; } = new();

    private NullOperatorScopeAuthorizer()
    {
    }

    public OperatorScopeDecision Evaluate(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation)
        => OperatorScopeDecision.NotGoverned();
}
