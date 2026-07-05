// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.VersionManagementServer;

/// <summary>
/// Access-control helpers for the VersionManagementServer branch-version surface (BH3-002/003/004).
/// Centralizes the two version authorization policies so every VMS endpoint applies them
/// consistently and the logic is unit-testable without a running server.
/// </summary>
internal static class VersionAccessPolicy
{
    /// <summary>
    /// Returns <see langword="true"/> when the caller identified by <paramref name="callerName"/>
    /// is permitted to see a version in list/info responses.
    /// </summary>
    /// <remarks>
    /// Visibility rules (mirror Esri branch-versioning spec):
    /// <list type="bullet">
    ///   <item>Public versions are visible to any query-access caller.</item>
    ///   <item>Protected versions are visible to any query-access caller (edit is owner-only).</item>
    ///   <item>Private versions are visible only to their owner and service administrators.</item>
    /// </list>
    /// Service write-access callers (lifecycle operations) also satisfy this check because
    /// write implies at least query access.
    /// </remarks>
    /// <param name="version">The version record to evaluate.</param>
    /// <param name="callerName">
    /// The authenticated principal's name (from <c>ClaimTypes.Name</c>), or
    /// <see langword="null"/> for anonymous callers.
    /// </param>
    /// <param name="isAdmin">
    /// Whether the caller holds the administrator role. Admins always see every version.
    /// </param>
    /// <returns><see langword="true"/> when the version is visible to the caller.</returns>
    internal static bool IsVersionVisible(GdbVersion version, string? callerName, bool isAdmin)
        => isAdmin
           || version.Access != VersionAccess.Private
           || string.Equals(version.Owner, callerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the caller is authorized to perform lifecycle
    /// operations (delete, alter, reconcile, post, conflict inspection/resolution,
    /// start/stop editing/reading) on the version.
    /// </summary>
    /// <remarks>
    /// Only the version owner and service administrators may perform lifecycle operations,
    /// regardless of the version's access level. This matches Esri's VMS behavior where
    /// portal admins and the version owner are the only principals allowed to mutate or
    /// reconcile/post a version they did not create.
    /// </remarks>
    /// <param name="version">The version record to evaluate.</param>
    /// <param name="callerName">The authenticated principal's name, or <see langword="null"/>.</param>
    /// <param name="isAdmin">Whether the caller holds the administrator role.</param>
    /// <returns><see langword="true"/> when the caller may manage the version.</returns>
    internal static bool CanManageVersion(GdbVersion version, string? callerName, bool isAdmin)
        => isAdmin
           || string.Equals(version.Owner, callerName, StringComparison.OrdinalIgnoreCase);
}
