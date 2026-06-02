// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// The canonical per-operation permission resolver — the single shared
/// authorization seam (#1375). Maps a <c>(principal, service, layer, operation)</c>
/// tuple to an allow / deny / requires-authentication decision by evaluating the
/// principal's <see cref="EffectivePermissions"/> from <see cref="IRoleStore"/>.
/// </summary>
/// <remarks>
/// This resolver is the source of per-operation access decisions for every
/// protocol. The existing coarse seams (<c>AccessPolicyEvaluator</c> /
/// <c>ServiceDataEditorAuthorization</c>) consult it first and fall back to the
/// legacy <c>AccessPolicy</c> only when it reports
/// <see cref="PermissionResult.NoMatchingGrant"/>, so unconfigured services keep
/// their current behavior.
/// </remarks>
public interface IPermissionResolver
{
    /// <summary>
    /// Resolves whether the supplied effective permissions authorize an
    /// operation on a service/layer.
    /// </summary>
    /// <param name="effectivePermissions">
    /// The principal's resolved permissions (from
    /// <see cref="IRoleStore.GetEffectivePermissionsAsync"/>).
    /// </param>
    /// <param name="service">The service name being accessed.</param>
    /// <param name="layer">
    /// The layer identifier being accessed, or <see langword="null"/> for a
    /// service-scoped check (treated as the wildcard layer).
    /// </param>
    /// <param name="operation">The operation being performed.</param>
    /// <returns>
    /// An <see cref="PermissionResult.Allow"/> decision (with the matched grant)
    /// when a grant covers the tuple, otherwise
    /// <see cref="PermissionResult.NoMatchingGrant"/>.
    /// </returns>
    PermissionDecision Authorize(
        EffectivePermissions effectivePermissions,
        string service,
        string? layer,
        AuthorizationOperation operation);

    /// <summary>
    /// Resolves a per-operation permission for a principal by first loading the
    /// principal's effective permissions and then evaluating the
    /// <c>(service, layer, operation)</c> tuple.
    /// </summary>
    /// <param name="userId">The principal's stable identifier.</param>
    /// <param name="roles">The role names the principal is a member of.</param>
    /// <param name="service">The service name being accessed.</param>
    /// <param name="layer">
    /// The layer identifier being accessed, or <see langword="null"/> for a
    /// service-scoped check.
    /// </param>
    /// <param name="operation">The operation being performed.</param>
    /// <param name="isAuthenticated">
    /// Whether the principal is authenticated. When <see langword="false"/> and
    /// no grant matches, the decision is
    /// <see cref="PermissionResult.RequiresAuthentication"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved permission decision.</returns>
    Task<PermissionDecision> AuthorizeAsync(
        string userId,
        IReadOnlyList<string> roles,
        string service,
        string? layer,
        AuthorizationOperation operation,
        bool isAuthenticated,
        CancellationToken cancellationToken = default);
}
