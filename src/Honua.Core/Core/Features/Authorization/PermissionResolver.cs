// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

public sealed class PermissionResolver : IPermissionResolver
{
    private readonly IRoleStore _roleStore;

    public PermissionResolver(IRoleStore roleStore)
    {
        ArgumentNullException.ThrowIfNull(roleStore);
        _roleStore = roleStore;
    }

    public PermissionDecision Authorize(
        EffectivePermissions effectivePermissions,
        string service,
        string? layer,
        AuthorizationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(effectivePermissions);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var requestedLayer = string.IsNullOrWhiteSpace(layer)
            ? AuthorizationOperationExtensions.Wildcard
            : layer.Trim();

        var matchedGrant = effectivePermissions.Permissions
            .FirstOrDefault(grant => Matches(grant, service, requestedLayer, operation));

        return matchedGrant is not null
            ? PermissionDecision.Allow(matchedGrant)
            : PermissionDecision.NoMatch();
    }

    public async Task<PermissionDecision> AuthorizeAsync(
        string userId,
        IReadOnlyList<string> roles,
        string service,
        string? layer,
        AuthorizationOperation operation,
        bool isAuthenticated,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(roles);

        var effective = await _roleStore
            .GetEffectivePermissionsAsync(userId ?? string.Empty, roles, cancellationToken)
            .ConfigureAwait(false);

        var decision = Authorize(effective, service, layer, operation);
        if (decision.IsAllowed)
        {
            return decision;
        }

        // No grant matched. If the principal is anonymous, surface a
        // requires-authentication signal so the caller can challenge rather than
        // forbid; otherwise let the caller decide on its coarse fallback.
        return isAuthenticated
            ? PermissionDecision.NoMatch()
            : PermissionDecision.RequiresAuthentication();
    }

    private static bool Matches(
        PermissionGrant grant,
        string service,
        string requestedLayer,
        AuthorizationOperation operation)
    {
        if (!MatchesScope(grant.Service, service))
        {
            return false;
        }

        if (!MatchesScope(grant.Layer, requestedLayer))
        {
            return false;
        }

        return AuthorizationOperationExtensions.Satisfies(grant.Operation, operation);
    }

    private static bool MatchesScope(string? grantValue, string requested)
    {
        if (string.IsNullOrWhiteSpace(grantValue))
        {
            return false;
        }

        var grant = grantValue.Trim();
        return grant == AuthorizationOperationExtensions.Wildcard
            || string.Equals(grant, requested, StringComparison.OrdinalIgnoreCase);
    }
}
