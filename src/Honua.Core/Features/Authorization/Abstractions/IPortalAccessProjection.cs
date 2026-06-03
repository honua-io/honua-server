// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// The single shared projection (#1370) that turns a canonical authorization
/// outcome into the ArcGIS Portal facade's notion of item/service
/// <see cref="PortalAccessLevel"/> and per-principal visibility.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam consumed by both the Portal/Sharing read surface (#1243) and
/// the OAuth2 named-user bridge (#1242). Implementing it once here guarantees the
/// <c>access</c> a Portal item reports always reflects the real RBAC decision
/// (<see cref="IPermissionResolver"/>) and the coarse <c>AccessPolicy</c> seam
/// (#349) — no parallel ACL model is introduced.
/// </para>
/// <para>
/// The projection is intentionally pure: it operates on resolved
/// <see cref="AccessPolicy"/> values and a precomputed
/// <see cref="PermissionDecision"/>, so it carries no <c>HttpContext</c> or
/// provider dependency and is safe to call from any protocol slice.
/// </para>
/// </remarks>
public interface IPortalAccessProjection
{
    /// <summary>
    /// Gets a value indicating whether the Portal access projection is enabled.
    /// When <see langword="false"/> the facade must not surface a discovery
    /// surface and consumers should treat every item as not visible.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Projects the effective access policy of a resource onto the Portal
    /// <see cref="PortalAccessLevel"/>. This is the level reported on the item
    /// regardless of who is asking (Esri's <c>access</c> field).
    /// </summary>
    /// <param name="layerPolicy">The resource (layer/item) access policy, or
    /// <see langword="null"/> when none is configured.</param>
    /// <param name="servicePolicy">The owning service access policy, or
    /// <see langword="null"/>.</param>
    /// <returns>The projected access level.</returns>
    PortalAccessLevel ProjectAccessLevel(AccessPolicy? layerPolicy, AccessPolicy? servicePolicy);

    /// <summary>
    /// Computes whether a specific principal may see/open a Portal item, folding
    /// the per-operation RBAC decision over the coarse access policy. This is the
    /// per-item visibility used to filter <c>search</c> results and authorize
    /// <c>content/items/{id}</c>.
    /// </summary>
    /// <param name="grantDecision">The per-operation decision produced by
    /// <see cref="IPermissionResolver"/> for the (principal, service, layer,
    /// query) tuple, or <see langword="null"/> when no resolver was consulted.</param>
    /// <param name="coarseDecision">The coarse <see cref="AccessDecision"/> from
    /// the <c>AccessPolicy</c> seam for the same principal/resource.</param>
    /// <returns>The resolved item visibility for the principal.</returns>
    PortalItemVisibility ProjectVisibility(PermissionDecision? grantDecision, AccessDecision coarseDecision);
}

/// <summary>
/// The per-principal visibility of a Portal item, the projection of an
/// authorization decision onto the facade's discovery/open semantics.
/// </summary>
/// <param name="IsVisible"><see langword="true"/> when the principal may see the
/// item in listings and open its metadata.</param>
/// <param name="RequiresAuthentication"><see langword="true"/> when the item is
/// hidden only because the caller is anonymous and authenticating could reveal
/// it; lets the facade emit a 401 challenge rather than silently omitting it.</param>
public readonly record struct PortalItemVisibility(bool IsVisible, bool RequiresAuthentication)
{
    /// <summary>Item is visible to the principal.</summary>
    public static PortalItemVisibility Visible => new(true, false);

    /// <summary>Item is hidden; the principal is forbidden.</summary>
    public static PortalItemVisibility Hidden => new(false, false);

    /// <summary>Item is hidden because authentication is required first.</summary>
    public static PortalItemVisibility AuthenticationRequired => new(false, true);
}
