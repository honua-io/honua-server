// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization;

/// <summary>
/// Default <see cref="IPermissionResolver"/>: evaluates a principal's
/// <see cref="EffectivePermissions"/> against a <c>(service, layer, operation)</c>
/// tuple using wildcard-aware matching (#1375).
/// </summary>
/// <remarks>
/// Matching rules:
/// <list type="bullet">
/// <item>A grant <see cref="PermissionGrant.Service"/> of <c>"*"</c> matches any
/// service; otherwise the service name must match case-insensitively.</item>
/// <item>A grant <see cref="PermissionGrant.Layer"/> of <c>"*"</c> matches any
/// layer (so a service-level grant implies all layers); otherwise the layer must
/// match case-insensitively. A <see langword="null"/> requested layer is treated
/// as the wildcard layer (service-scoped check).</item>
/// <item>The grant <see cref="PermissionGrant.Operation"/> is matched via
/// <see cref="AuthorizationOperationExtensions.Satisfies"/> (wildcard and
/// read/query synonym aware).</item>
/// </list>
/// The resolver never returns a hard "deny": when no grant matches it reports
/// <see cref="PermissionResult.NoMatchingGrant"/> so the calling seam can fall
/// back to the coarse <c>AccessPolicy</c> and preserve current behavior for
/// services that have no per-operation grants configured.
/// </remarks>
public sealed class PermissionResolver : IPermissionResolver
{
    private readonly IRoleStore _roleStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermissionResolver"/> class.
    /// </summary>
    /// <param name="roleStore">The role/permission store used to resolve
    /// effective permissions for the async overload.</param>
    public PermissionResolver(IRoleStore roleStore)
    {
        ArgumentNullException.ThrowIfNull(roleStore);
        _roleStore = roleStore;
    }

    /// <inheritdoc />
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

        foreach (var grant in effectivePermissions.Permissions)
        {
            if (Matches(grant, service, requestedLayer, operation))
            {
                return PermissionDecision.Allow(grant);
            }
        }

        return PermissionDecision.NoMatch();
    }

    /// <inheritdoc />
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
