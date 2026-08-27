// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory role store for the admin API surface.
/// Will be replaced by a persistent implementation when #498 lands.
/// </summary>
internal sealed class InMemoryRoleStore : IRoleStore
{
    private readonly object _mutationGate = new();
    private readonly ConcurrentDictionary<Guid, RoleDefinition> _roles = new();
    private readonly Dictionary<string, Guid> _roleIdsByName = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryRoleStore()
    {
        // Seed built-in roles
        var adminRole = new RoleDefinition
        {
            RoleId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Name = "admin",
            Description = "Full administrative access",
            IsBuiltIn = true,
            Permissions =
            [
                new PermissionGrant { Service = "*", Layer = "*", Operation = "*" },
            ],
        };

        var viewerRole = new RoleDefinition
        {
            RoleId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Name = "viewer",
            Description = "Read-only access to all services and layers",
            IsBuiltIn = true,
            Permissions =
            [
                new PermissionGrant { Service = "*", Layer = "*", Operation = "read" },
            ],
        };

        _roles[adminRole.RoleId] = adminRole;
        _roles[viewerRole.RoleId] = viewerRole;
        _roleIdsByName[adminRole.Name] = adminRole.RoleId;
        _roleIdsByName[viewerRole.Name] = viewerRole.RoleId;
    }

    public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RoleDefinition> result = _roles.Values.ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        _roles.TryGetValue(roleId, out var role);
        return Task.FromResult(role);
    }

    public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            if (_roles.ContainsKey(role.RoleId))
            {
                throw new InvalidOperationException($"Role with ID '{role.RoleId}' already exists.");
            }

            if (_roleIdsByName.ContainsKey(role.Name))
            {
                throw new InvalidOperationException($"Role with name '{role.Name}' already exists.");
            }

            _roles[role.RoleId] = role;
            _roleIdsByName.Add(role.Name, role.RoleId);
        }

        return Task.FromResult(role);
    }

    public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            if (!_roles.TryGetValue(role.RoleId, out var existing))
            {
                return Task.FromResult<RoleDefinition?>(null);
            }

            if (!string.Equals(role.Name, existing.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Role names cannot be changed after creation.");
            }

            _roles[role.RoleId] = role;
            return Task.FromResult<RoleDefinition?>(role);
        }
    }

    public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            if (!_roles.TryGetValue(roleId, out var role) || role.IsBuiltIn)
            {
                return Task.FromResult(false);
            }

            if (!_roles.TryRemove(roleId, out _))
            {
                return Task.FromResult(false);
            }

            // Preserve the global name reservation after deletion. Managed-user
            // memberships retain role names, so reusing a deleted name could attach
            // stale members to a different role and permission set.
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        if (!_roles.TryGetValue(roleId, out var role))
        {
            return Task.FromResult<IReadOnlyList<PermissionGrant>>([]);
        }

        return Task.FromResult(role.Permissions);
    }

    public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default)
    {
        lock (_mutationGate)
        {
            if (!_roles.TryGetValue(roleId, out var existing))
            {
                return Task.FromResult<IReadOnlyList<PermissionGrant>>([]);
            }

            var updated = new RoleDefinition
            {
                RoleId = existing.RoleId,
                Name = existing.Name,
                Description = existing.Description,
                IsBuiltIn = existing.IsBuiltIn,
                Permissions = permissions,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _roles[roleId] = updated;
            return Task.FromResult(permissions);
        }
    }

    public Task<EffectivePermissions> GetEffectivePermissionsAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        var allPermissions = _roles.Values
            .Where(r => roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .SelectMany(r => r.Permissions)
            .Distinct()
            .ToList();

        var result = new EffectivePermissions
        {
            UserId = userId,
            Roles = roles,
            Permissions = allPermissions,
            ResolvedAt = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(result);
    }
}
