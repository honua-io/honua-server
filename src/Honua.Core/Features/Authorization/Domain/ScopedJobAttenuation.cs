// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Pure, server-side attenuation math for the custom-code geoprocessing auth spine
/// (Phase 0). Computes the <em>intersection</em> of an owner's reachable grants and
/// a requested resource scope, and projects that intersection onto the role and
/// permission claims the shared RBAC pipeline already enforces.
/// </summary>
/// <remarks>
/// <para>
/// This type carries no I/O and no <c>HttpContext</c> dependency so it is safe to
/// call from any layer (submit-time clamp in the geoprocessing service and
/// mint-time freeze in the issuer share this one implementation). It is the single
/// place the narrow-only invariant is computed; both callers MUST route through it
/// so the rule cannot drift between the two sites.
/// </para>
/// <para>
/// Owner reachability is derived from the same claim grammar the live pipeline
/// reads: the <c>admin</c> role (full authority), configured global data-editor
/// roles, service-scoped <c>data-editor:{service}</c> roles (service-wide write),
/// and per-resource <c>read:</c>/<c>write:</c> permission grants. A scope entry is
/// only retained when the owner could actually reach it at the requested access
/// level — anything else is dropped (clamped away), never widened.
/// </para>
/// </remarks>
public static class ScopedJobAttenuation
{
    /// <summary>The conventional full-authority role.</summary>
    public const string AdminRole = "admin";

    /// <summary>Prefix for service-scoped data-editor roles (mirrors <c>RbacOptions.DataEditorServicePrefix</c> default).</summary>
    public const string ServiceEditorRolePrefix = "data-editor:";

    /// <summary>Permission-grant prefix for write access (mirrors the layer-scoped write-key grammar).</summary>
    public const string WriteGrantPrefix = "write:";

    /// <summary>Permission-grant prefix for read access (the read counterpart of <see cref="WriteGrantPrefix"/>).</summary>
    public const string ReadGrantPrefix = "read:";

    /// <summary>
    /// Computes the effective (intersected) scope: every requested entry the owner
    /// could actually reach at the requested access level, clamped to the owner's
    /// reachable access. An entry the owner could only read but the request asked
    /// to write is downgraded to read; an entry the owner could not reach at all is
    /// dropped. The result can therefore never exceed either input.
    /// </summary>
    /// <param name="ownerRoles">The owner's role snapshot.</param>
    /// <param name="ownerGrants">
    /// The owner's per-resource permission grants (<c>read:{svc}[/{layer}]</c> /
    /// <c>write:{svc}[/{layer}]</c>), or <see langword="null"/>/empty when the owner
    /// carries no fine-grained grants.
    /// </param>
    /// <param name="globalDataEditorRoles">
    /// Roles that confer write to every service (the configured global data-editor
    /// roles), or <see langword="null"/> when none are configured.
    /// </param>
    /// <param name="requestedScope">The scope the job declared / requested.</param>
    /// <returns>The intersected, access-clamped scope (possibly empty).</returns>
    public static IReadOnlyList<JobResourceScopeEntry> Intersect(
        IReadOnlyList<string>? ownerRoles,
        IReadOnlyList<string>? ownerGrants,
        IReadOnlyList<string>? globalDataEditorRoles,
        IReadOnlyList<JobResourceScopeEntry>? requestedScope)
    {
        if (requestedScope is null || requestedScope.Count == 0)
        {
            return [];
        }

        var reach = OwnerReach.From(ownerRoles, ownerGrants, globalDataEditorRoles);

        var effective = new List<JobResourceScopeEntry>(requestedScope.Count);
        foreach (var entry in requestedScope)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.ServiceId))
            {
                continue;
            }

            var service = entry.ServiceId.Trim();
            var layer = string.IsNullOrWhiteSpace(entry.LayerId) ? null : entry.LayerId!.Trim();

            var reachable = reach.MaxAccess(service, layer);
            if (reachable is null)
            {
                // Owner cannot reach this resource at all — drop it (narrow-only).
                continue;
            }

            // Clamp the requested access down to what the owner could reach. A
            // requested Write against a read-only-reachable resource degrades to Read.
            var granted = entry.Access == JobResourceAccess.Write && reachable == JobResourceAccess.Write
                ? JobResourceAccess.Write
                : JobResourceAccess.Read;

            effective.Add(new JobResourceScopeEntry(service, layer, granted));
        }

        return effective;
    }

    /// <summary>
    /// Determines whether a requested scope is wholly reachable by the owner — i.e.
    /// the intersection equals the request (every entry retained at the requested
    /// access). Used by the submit-time validation to reject (rather than silently
    /// clamp) a declared scope that exceeds the submitter.
    /// </summary>
    /// <param name="ownerRoles">The owner's role snapshot.</param>
    /// <param name="ownerGrants">The owner's per-resource permission grants.</param>
    /// <param name="globalDataEditorRoles">Roles conferring write to every service.</param>
    /// <param name="requestedScope">The declared scope to test.</param>
    /// <param name="firstUnreachable">The first entry the owner cannot fully reach, when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every entry is reachable at its requested access.</returns>
    public static bool IsWithinOwner(
        IReadOnlyList<string>? ownerRoles,
        IReadOnlyList<string>? ownerGrants,
        IReadOnlyList<string>? globalDataEditorRoles,
        IReadOnlyList<JobResourceScopeEntry>? requestedScope,
        out JobResourceScopeEntry? firstUnreachable)
    {
        firstUnreachable = null;
        if (requestedScope is null || requestedScope.Count == 0)
        {
            return true;
        }

        var reach = OwnerReach.From(ownerRoles, ownerGrants, globalDataEditorRoles);
        foreach (var entry in requestedScope)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.ServiceId))
            {
                firstUnreachable = entry;
                return false;
            }

            var service = entry.ServiceId.Trim();
            var layer = string.IsNullOrWhiteSpace(entry.LayerId) ? null : entry.LayerId!.Trim();
            var reachable = reach.MaxAccess(service, layer);

            if (reachable is null ||
                (entry.Access == JobResourceAccess.Write && reachable != JobResourceAccess.Write))
            {
                firstUnreachable = entry;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Projects an effective scope onto the role and permission claim values the
    /// shared RBAC pipeline enforces. The returned values are what the issuer pins
    /// onto the hydrated principal: <c>data-editor:{service}</c> roles for
    /// service-wide write entries, and <c>read:</c>/<c>write:</c> permission grants
    /// for every entry. Because only the intersected scope is projected, the
    /// hydrated principal can never out-grant the intersection.
    /// </summary>
    /// <param name="effectiveScope">The already-intersected scope.</param>
    /// <returns>The role and permission claim values to pin onto the principal.</returns>
    public static ScopedJobClaims ToClaims(IReadOnlyList<JobResourceScopeEntry> effectiveScope)
    {
        var roles = new List<string>();
        var permissions = new List<string>();

        if (effectiveScope is null)
        {
            return new ScopedJobClaims(roles, permissions);
        }

        foreach (var entry in effectiveScope)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.ServiceId))
            {
                continue;
            }

            var service = entry.ServiceId.Trim();
            var layer = string.IsNullOrWhiteSpace(entry.LayerId) ? null : entry.LayerId!.Trim();
            var target = layer is null ? service : string.Concat(service, "/", layer);

            // Always project a read grant; reads are the access floor.
            permissions.Add(string.Concat(ReadGrantPrefix, target));

            if (entry.Access == JobResourceAccess.Write)
            {
                permissions.Add(string.Concat(WriteGrantPrefix, target));

                // A service-wide write entry also satisfies the service-scoped
                // data-editor role gate; a layer-specific write does not widen to
                // the whole service, so only the scoped permission grant is added.
                if (layer is null)
                {
                    roles.Add(string.Concat(ServiceEditorRolePrefix, service));
                }
            }
        }

        return new ScopedJobClaims(roles, permissions);
    }

    /// <summary>
    /// Pre-indexed view of an owner's reachable access for fast per-entry checks.
    /// </summary>
    private sealed class OwnerReach
    {
        private readonly bool _isAdminOrGlobalEditor;
        // service (lower) -> max access for the whole service
        private readonly Dictionary<string, JobResourceAccess> _serviceAccess = new(StringComparer.OrdinalIgnoreCase);
        // "service/layer" (lower) -> max access for that layer
        private readonly Dictionary<string, JobResourceAccess> _layerAccess = new(StringComparer.OrdinalIgnoreCase);

        private OwnerReach(bool isAdminOrGlobalEditor)
        {
            _isAdminOrGlobalEditor = isAdminOrGlobalEditor;
        }

        public static OwnerReach From(
            IReadOnlyList<string>? roles,
            IReadOnlyList<string>? grants,
            IReadOnlyList<string>? globalDataEditorRoles)
        {
            var admin = false;

            HashSet<string>? globalEditors = null;
            if (globalDataEditorRoles is { Count: > 0 })
            {
                globalEditors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in globalDataEditorRoles)
                {
                    if (!string.IsNullOrWhiteSpace(r))
                    {
                        globalEditors.Add(r.Trim());
                    }
                }
            }

            var serviceWideWriteRoles = new List<string>();
            if (roles is not null)
            {
                foreach (var raw in roles)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var role = raw.Trim();

                    if (string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase) ||
                        (globalEditors is not null && globalEditors.Contains(role)))
                    {
                        admin = true;
                        continue;
                    }

                    if (role.StartsWith(ServiceEditorRolePrefix, StringComparison.OrdinalIgnoreCase) &&
                        role.Length > ServiceEditorRolePrefix.Length)
                    {
                        var svc = role[ServiceEditorRolePrefix.Length..].Trim();
                        if (svc.Length > 0)
                        {
                            serviceWideWriteRoles.Add(svc);
                        }
                    }
                }
            }

            var reach = new OwnerReach(admin);

            foreach (var svc in serviceWideWriteRoles)
            {
                reach.AddServiceAccess(svc, JobResourceAccess.Write);
            }

            if (grants is not null)
            {
                foreach (var raw in grants)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var grant = raw.Trim();
                    JobResourceAccess access;
                    string target;
                    if (grant.StartsWith(WriteGrantPrefix, StringComparison.OrdinalIgnoreCase) &&
                        grant.Length > WriteGrantPrefix.Length)
                    {
                        access = JobResourceAccess.Write;
                        target = grant[WriteGrantPrefix.Length..].Trim();
                    }
                    else if (grant.StartsWith(ReadGrantPrefix, StringComparison.OrdinalIgnoreCase) &&
                        grant.Length > ReadGrantPrefix.Length)
                    {
                        access = JobResourceAccess.Read;
                        target = grant[ReadGrantPrefix.Length..].Trim();
                    }
                    else
                    {
                        continue;
                    }

                    if (target.Length == 0)
                    {
                        continue;
                    }

                    var sep = target.IndexOf('/');
                    if (sep < 0)
                    {
                        reach.AddServiceAccess(target, access);
                    }
                    else
                    {
                        var svc = target[..sep].Trim();
                        var layer = target[(sep + 1)..].Trim();
                        if (svc.Length > 0 && layer.Length > 0)
                        {
                            reach.AddLayerAccess(svc, layer, access);
                        }
                    }
                }
            }

            return reach;
        }

        private void AddServiceAccess(string service, JobResourceAccess access)
        {
            if (!_serviceAccess.TryGetValue(service, out var existing) || access > existing)
            {
                _serviceAccess[service] = access;
            }
        }

        private void AddLayerAccess(string service, string layer, JobResourceAccess access)
        {
            var key = string.Concat(service, "/", layer);
            if (!_layerAccess.TryGetValue(key, out var existing) || access > existing)
            {
                _layerAccess[key] = access;
            }
        }

        /// <summary>
        /// Returns the maximum access the owner can reach for the
        /// <c>(service, layer)</c> target, or <see langword="null"/> when the owner
        /// cannot reach it at all.
        /// </summary>
        public JobResourceAccess? MaxAccess(string service, string? layer)
        {
            if (_isAdminOrGlobalEditor)
            {
                return JobResourceAccess.Write;
            }

            JobResourceAccess? best = null;

            if (_serviceAccess.TryGetValue(service, out var svcAccess))
            {
                best = Max(best, svcAccess);
            }

            if (layer is null)
            {
                // A service-wide request is only reachable by a service-wide grant
                // (a single-layer grant does not cover the whole service).
                return best;
            }

            var layerKey = string.Concat(service, "/", layer);
            if (_layerAccess.TryGetValue(layerKey, out var layerAccess))
            {
                best = Max(best, layerAccess);
            }

            return best;
        }

        private static JobResourceAccess Max(JobResourceAccess? current, JobResourceAccess candidate)
            => current is null || candidate > current ? candidate : current.Value;
    }
}

/// <summary>
/// The role and permission claim values an attenuated scoped-job principal carries,
/// produced by <see cref="ScopedJobAttenuation.ToClaims"/>.
/// </summary>
/// <param name="Roles">Service-scoped <c>data-editor:{service}</c> roles for service-wide write entries.</param>
/// <param name="Permissions">Per-resource <c>read:</c>/<c>write:</c> permission grants for every entry.</param>
public sealed record ScopedJobClaims(
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
