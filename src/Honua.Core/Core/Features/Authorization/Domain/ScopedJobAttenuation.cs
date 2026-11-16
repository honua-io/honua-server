// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public static class ScopedJobAttenuation
{
    public const string AdminRole = "admin";

    public const string ServiceEditorRolePrefix = "data-editor:";

    public const string WriteGrantPrefix = "write:";

    public const string ReadGrantPrefix = "read:";

    public const string ReadWildcard = "*";

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

    private sealed class OwnerReach
    {
        private readonly bool _isAdminOrGlobalEditor;

        private bool _readsEveryService;
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
                foreach (var r in globalDataEditorRoles.Where(r => !string.IsNullOrWhiteSpace(r)))
                {
                    globalEditors.Add(r.Trim());
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
                        // read:* reaches every service at read level; there is no write wildcard.
                        if (access == JobResourceAccess.Read && target == ReadWildcard)
                        {
                            reach._readsEveryService = true;
                            continue;
                        }

                        reach.AddServiceAccess(target, access);
                    }
                    else
                    {
                        var svc = target[..sep].Trim();
                        var layer = target[(sep + 1)..].Trim();
                        if (access == JobResourceAccess.Read && layer == ReadWildcard && svc.Length > 0 && svc != ReadWildcard)
                        {
                            // read:{service}/* is the service-wide read:{service} grant.
                            reach.AddServiceAccess(svc, access);
                        }
                        else if (svc.Length > 0 && layer.Length > 0)
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

        public JobResourceAccess? MaxAccess(string service, string? layer)
        {
            if (_isAdminOrGlobalEditor)
            {
                return JobResourceAccess.Write;
            }

            JobResourceAccess? best = _readsEveryService ? JobResourceAccess.Read : null;

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

public sealed record ScopedJobClaims(
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
