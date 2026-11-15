// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization;

public sealed class PortalAccessProjection
{
    private readonly bool _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="PortalAccessProjection"/> class.
    /// </summary>
    /// <param name="enabled">
    /// Whether the Portal access projection is enabled. Defaults to
    /// <see langword="false"/> so the facade stays off-by-default and never
    /// exposes a discovery surface unless an operator opts in (#1370).
    /// </param>
    public PortalAccessProjection(bool enabled = false)
    {
        _enabled = enabled;
    }

    public bool IsEnabled => _enabled;

    public PortalAccessLevel ProjectAccessLevel(AccessPolicy? layerPolicy, AccessPolicy? servicePolicy)
    {
        var layer = ProjectSingle(layerPolicy);
        var service = ProjectSingle(servicePolicy);

        // A layer can only narrow what the service exposes: the effective level is
        // the more restrictive of the two when both are configured. When the layer
        // has no policy of its own it inherits the service level.
        if (layerPolicy is null)
        {
            return service;
        }

        if (servicePolicy is null)
        {
            return layer;
        }

        return layer < service ? layer : service;
    }

    public PortalItemVisibility ProjectVisibility(PermissionDecision? grantDecision, AccessDecision coarseDecision)
    {
        // An explicit per-operation grant authorizes discovery/open directly.
        if (grantDecision is { Result: PermissionResult.Allow })
        {
            return PortalItemVisibility.Visible;
        }

        // The resolver can also surface a hard "authenticate first" signal for an
        // anonymous principal even before the coarse policy is consulted.
        if (grantDecision is { Result: PermissionResult.RequiresAuthentication })
        {
            return PortalItemVisibility.AuthenticationRequired;
        }

        // No matching grant (or resolver not consulted): the coarse policy governs.
        if (coarseDecision.IsAllowed)
        {
            return PortalItemVisibility.Visible;
        }

        return coarseDecision.RequiresAuthentication
            ? PortalItemVisibility.AuthenticationRequired
            : PortalItemVisibility.Hidden;
    }

    private static PortalAccessLevel ProjectSingle(AccessPolicy? policy)
    {
        if (policy is null)
        {
            // No policy means the resource is not exposed publicly; the facade
            // treats it as owner/grant-only until a policy opens it.
            return PortalAccessLevel.Private;
        }

        if (policy.AllowAnonymous)
        {
            return PortalAccessLevel.Public;
        }

        // Authenticated access with no role restriction is "org": any signed-in
        // member of the organization may read it.
        var hasRoleRestriction = policy.AllowedRoles is { Length: > 0 };
        return hasRoleRestriction ? PortalAccessLevel.Private : PortalAccessLevel.Organization;
    }
}
