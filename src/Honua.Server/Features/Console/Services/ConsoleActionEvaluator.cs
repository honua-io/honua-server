// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Console.Services;

/// <summary>
/// Default <see cref="IConsoleActionEvaluator"/> implementation.
/// </summary>
/// <remarks>
/// Maps Console's seven verbs onto the existing <see cref="IRoleStore"/>
/// permission grant set and the principal's claims. The mapping is documented
/// inline so behavioural gaps surface in review.
/// </remarks>
internal sealed class ConsoleActionEvaluator(
    IRoleStore roleStore,
    IOptions<RbacOptions> rbacOptions,
    ILogger<ConsoleActionEvaluator> logger) : IConsoleActionEvaluator
{
    private static readonly IReadOnlyList<ConsoleContentAction> AllActions =
    [
        ConsoleContentAction.View,
        ConsoleContentAction.Edit,
        ConsoleContentAction.Publish,
        ConsoleContentAction.Share,
        ConsoleContentAction.Embed,
        ConsoleContentAction.Operate,
        ConsoleContentAction.Administer,
    ];

    /// <summary>
    /// Default Console route keys; entitlements are resolved against capabilities.
    /// </summary>
    public static IReadOnlyList<string> DefaultRouteKeys { get; } =
    [
        "catalog",
        "studio",
        "operate",
        "share",
        "admin",
    ];

    public async Task<IReadOnlyList<ConsoleContentAction>> EvaluateItemActionsAsync(
        ClaimsPrincipal principal,
        ConsoleContentItem item,
        IReadOnlyList<ConsoleContentAction> candidateActions,
        CancellationToken cancellationToken = default)
    {
        var profile = await ResolveAuthorizationProfileAsync(principal, cancellationToken).ConfigureAwait(false);
        return EvaluateForItem(item, profile, candidateActions);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ConsoleContentAction>>> EvaluateItemActionsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<ConsoleContentItem> items,
        IReadOnlyList<ConsoleContentAction> candidateActions,
        CancellationToken cancellationToken = default)
    {
        var profile = await ResolveAuthorizationProfileAsync(principal, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, IReadOnlyList<ConsoleContentAction>>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            results[item.Id] = EvaluateForItem(item, profile, candidateActions);
        }
        return results;
    }

    public async Task<IReadOnlyList<ConsoleNavigationEntitlement>> EvaluateRouteEntitlementsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<string> routeKeys,
        CancellationToken cancellationToken = default)
    {
        var profile = await ResolveAuthorizationProfileAsync(principal, cancellationToken).ConfigureAwait(false);
        var entitlements = new List<ConsoleNavigationEntitlement>(routeKeys.Count);
        foreach (var routeKey in routeKeys)
        {
            entitlements.Add(EvaluateRoute(routeKey, profile));
        }
        ConsoleAuthorizationLog.RouteEntitlementsEvaluated(logger, profile.UserId, routeKeys.Count);
        return entitlements;
    }

    public async Task<IReadOnlyList<string>> ResolveCapabilitiesAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var profile = await ResolveAuthorizationProfileAsync(principal, cancellationToken).ConfigureAwait(false);
        return profile.Capabilities.ToList();
    }

    private List<ConsoleContentAction> EvaluateForItem(
        ConsoleContentItem item,
        AuthorizationProfile profile,
        IReadOnlyList<ConsoleContentAction> candidateActions)
    {
        var pool = candidateActions.Count == 0 ? AllActions : candidateActions;
        var allowed = new List<ConsoleContentAction>(pool.Count);
        foreach (var action in pool)
        {
            if (IsItemActionAllowed(item, profile, action))
            {
                allowed.Add(action);
            }
        }
        ConsoleAuthorizationLog.ItemActionsEvaluated(logger, profile.UserId, item.Id, allowed.Count);
        return allowed;
    }

    private static bool IsItemActionAllowed(ConsoleContentItem item, AuthorizationProfile profile, ConsoleContentAction action)
    {
        if (profile.IsAdmin)
            return true;

        return action switch
        {
            ConsoleContentAction.View => CanView(item, profile),
            ConsoleContentAction.Edit => CanEdit(item, profile),
            ConsoleContentAction.Publish => HasCapability(profile, "catalog.publish") || profile.IsAdmin,
            ConsoleContentAction.Share => CanShare(item, profile),
            ConsoleContentAction.Embed => CanEmbed(item, profile),
            ConsoleContentAction.Operate => CanOperate(item, profile),
            ConsoleContentAction.Administer => HasCapability(profile, "admin.rbac.write"),
            _ => false,
        };
    }

    // Visibility is a terminal gate for non-admins: a principal that cannot see
    // an item cannot view, edit, share, embed, or operate on it either. Admin
    // bypass is handled at the top of IsItemActionAllowed.
    private static bool CanView(ConsoleContentItem item, AuthorizationProfile profile)
    {
        if (item.Visibility == ConsoleVisibility.Public)
            return true;

        if (!profile.IsAuthenticated)
            return false;

        if (IsOwner(item, profile))
            return true;

        return item.Visibility switch
        {
            ConsoleVisibility.Organization => true,
            ConsoleVisibility.Team => IsInTeamScope(item, profile),
            ConsoleVisibility.Personal => false,
            _ => false,
        };
    }

    private static bool CanEdit(ConsoleContentItem item, AuthorizationProfile profile)
    {
        if (!profile.IsAuthenticated)
            return false;

        if (IsOwner(item, profile))
            return true;

        return CanView(item, profile) && HasCapability(profile, "metadata.write");
    }

    private static bool CanShare(ConsoleContentItem item, AuthorizationProfile profile)
    {
        if (!profile.IsAuthenticated)
            return false;

        if (IsOwner(item, profile))
            return true;

        return CanView(item, profile) && HasCapability(profile, "metadata.write");
    }

    private static bool CanEmbed(ConsoleContentItem item, AuthorizationProfile profile)
    {
        return CanView(item, profile);
    }

    private static bool CanOperate(ConsoleContentItem item, AuthorizationProfile profile)
    {
        if (!profile.IsAuthenticated)
            return item.Visibility == ConsoleVisibility.Public && HasCapability(profile, "features.query");

        if (!CanView(item, profile))
            return false;

        return item.ItemType switch
        {
            ConsoleContentItemType.Service or ConsoleContentItemType.Layer
                or ConsoleContentItemType.SavedMap or ConsoleContentItemType.Dashboard
                or ConsoleContentItemType.OpenData
                => HasCapability(profile, "features.query") || HasCapability(profile, "raster.render"),
            ConsoleContentItemType.Report => HasCapability(profile, "reports.run") || HasCapability(profile, "metadata.read"),
            ConsoleContentItemType.GeneratedApp => true,
            _ => false,
        };
    }

    private static bool IsOwner(ConsoleContentItem item, AuthorizationProfile profile)
    {
        return !string.IsNullOrEmpty(profile.UserId)
            && string.Equals(item.OwnerId, profile.UserId, StringComparison.Ordinal);
    }

    private static bool IsInTeamScope(ConsoleContentItem item, AuthorizationProfile profile)
    {
        if (string.IsNullOrEmpty(item.TeamScopeId))
            return false;
        foreach (var scope in profile.TeamScopes)
        {
            if (string.Equals(scope, item.TeamScopeId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasCapability(AuthorizationProfile profile, string capability)
    {
        foreach (var c in profile.Capabilities)
        {
            if (string.Equals(c, capability, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static ConsoleNavigationEntitlement EvaluateRoute(string routeKey, AuthorizationProfile profile)
    {
        if (profile.IsAdmin)
        {
            return new ConsoleNavigationEntitlement { RouteKey = routeKey, Allowed = true };
        }

        var capabilityForRoute = routeKey switch
        {
            "catalog" => "catalog.read",
            "studio" => "studio.edit",
            "operate" => "features.query",
            "share" => "metadata.write",
            "admin" => "admin.rbac.write",
            _ => $"console.{routeKey}",
        };

        var allowed = HasCapability(profile, capabilityForRoute);
        return new ConsoleNavigationEntitlement
        {
            RouteKey = routeKey,
            Allowed = allowed,
            Reason = allowed ? null : "insufficient-capability",
        };
    }

    private async Task<AuthorizationProfile> ResolveAuthorizationProfileAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var isAuthenticated = principal.Identity is { IsAuthenticated: true };
        if (!isAuthenticated)
        {
            return AuthorizationProfile.Anonymous;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? principal.FindFirstValue("sub")
                     ?? string.Empty;

        var roleClaimType = rbacOptions.Value.EffectiveRoleClaimType;
        var checkStandardRoleClaim = !string.Equals(roleClaimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase);

        var roles = new List<string>();
        var teamScopes = new List<string>();
        var isAdmin = false;

        foreach (var claim in principal.Claims)
        {
            if (string.Equals(claim.Type, rbacOptions.Value.WorkspaceScopeClaimType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(claim.Value))
            {
                teamScopes.Add(claim.Value);
                continue;
            }

            var isRoleClaim = string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase)
                || (checkStandardRoleClaim && string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase));

            if (!isRoleClaim || string.IsNullOrWhiteSpace(claim.Value))
                continue;

            if (string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase))
            {
                isAdmin = true;
            }

            roles.Add(claim.Value);
        }

        var capabilities = new HashSet<string>(StringComparer.Ordinal);

        if (isAdmin)
        {
            capabilities.UnionWith(AdminCapabilitySet);
        }
        else if (roles.Count > 0)
        {
            var effective = await roleStore.GetEffectivePermissionsAsync(userId, roles, cancellationToken).ConfigureAwait(false);
            foreach (var grant in effective.Permissions)
            {
                AddCapabilityForGrant(capabilities, grant);
            }
        }

        ConsoleAuthorizationLog.ProfileResolved(logger, userId, capabilities.Count, isAdmin);
        return new AuthorizationProfile(userId, isAuthenticated, isAdmin, capabilities, teamScopes);
    }

    private static void AddCapabilityForGrant(HashSet<string> capabilities, PermissionGrant grant)
    {
        var operation = grant.Operation;
        switch (operation)
        {
            case "*":
            case "read":
                capabilities.Add("metadata.read");
                capabilities.Add("catalog.read");
                capabilities.Add("features.query");
                if (operation == "*")
                {
                    capabilities.Add("metadata.write");
                    capabilities.Add("studio.edit");
                    capabilities.Add("catalog.publish");
                    capabilities.Add("features.edit");
                    capabilities.Add("raster.render");
                    capabilities.Add("reports.run");
                    capabilities.Add("admin.rbac.write");
                }
                break;
            case "write":
            case "edit":
                capabilities.Add("metadata.read");
                capabilities.Add("metadata.write");
                capabilities.Add("studio.edit");
                capabilities.Add("features.edit");
                capabilities.Add("features.query");
                break;
            case "publish":
                capabilities.Add("metadata.read");
                capabilities.Add("metadata.write");
                capabilities.Add("catalog.publish");
                break;
            case "render":
                capabilities.Add("metadata.read");
                capabilities.Add("raster.render");
                capabilities.Add("features.query");
                break;
            case "query":
                capabilities.Add("metadata.read");
                capabilities.Add("features.query");
                break;
            case "report":
            case "run":
                capabilities.Add("metadata.read");
                capabilities.Add("reports.run");
                break;
            case "administer":
            case "admin":
                capabilities.Add("admin.rbac.write");
                capabilities.Add("metadata.write");
                capabilities.Add("metadata.read");
                break;
        }
    }

    private static readonly string[] AdminCapabilitySet =
    [
        "metadata.read",
        "metadata.write",
        "catalog.read",
        "catalog.publish",
        "studio.edit",
        "features.query",
        "features.edit",
        "raster.render",
        "reports.run",
        "admin.rbac.write",
    ];

    private sealed record AuthorizationProfile(
        string UserId,
        bool IsAuthenticated,
        bool IsAdmin,
        IReadOnlyCollection<string> Capabilities,
        IReadOnlyList<string> TeamScopes)
    {
        public static readonly AuthorizationProfile Anonymous = new(
            string.Empty,
            IsAuthenticated: false,
            IsAdmin: false,
            new HashSet<string>(StringComparer.Ordinal),
            Array.Empty<string>());
    }
}
