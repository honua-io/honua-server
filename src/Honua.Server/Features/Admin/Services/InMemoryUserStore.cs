// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory user store for the admin API surface. Also backs the SCIM 2.0 provisioning
/// surface (<see cref="IScimUserStore"/>, #510) over the same record set so users created by
/// an identity provider are immediately visible to the admin endpoints.
/// Will be replaced by a persistent implementation when #496/#498 land.
/// </summary>
internal sealed class InMemoryUserStore : IUserStore, IScimUserStore
{
    private readonly ConcurrentDictionary<string, ManagedUser> _users = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seeds a user into the store (for testing).
    /// </summary>
    internal void Seed(ManagedUser user) => _users[user.UserId] = user;

    public Task<UserListResult> ListUsersAsync(UserListFilter filter, CancellationToken cancellationToken = default)
    {
        IEnumerable<ManagedUser> query = _users.Values;

        if (!string.IsNullOrEmpty(filter.ProvisioningSource))
        {
            query = query.Where(u => u.ProvisioningSource.Equals(filter.ProvisioningSource, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(filter.Role))
        {
            query = query.Where(u => u.Roles.Contains(filter.Role, StringComparer.OrdinalIgnoreCase));
        }

        if (filter.IsActive.HasValue)
        {
            var isActive = filter.IsActive.Value;
            query = query.Where(u => u.IsActive == isActive);
        }

        var all = query.ToList();

        var result = new UserListResult
        {
            TotalCount = all.Count,
            Users = all.Skip(filter.Offset).Take(filter.Limit).ToList().AsReadOnly(),
        };

        return Task.FromResult(result);
    }

    public Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        _users.TryGetValue(userId, out var user);
        return Task.FromResult(user);
    }

    public Task<ManagedUser?> UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(userId, out var existing))
        {
            return Task.FromResult<ManagedUser?>(null);
        }

        var updated = new ManagedUser
        {
            UserId = existing.UserId,
            DisplayName = existing.DisplayName,
            Email = existing.Email,
            ProvisioningSource = existing.ProvisioningSource,
            ProviderId = existing.ProviderId,
            IsActive = existing.IsActive,
            Roles = NormalizeRoles(roles),
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _users[userId] = updated;
        return Task.FromResult<ManagedUser?>(updated);
    }

    public Task<bool> DeprovisionUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(userId, out var existing))
        {
            return Task.FromResult(false);
        }

        _users[userId] = Deactivate(existing);
        return Task.FromResult(true);
    }

    // ---- IScimUserStore (#510) -------------------------------------------------------

    public Task<ManagedUser?> CreateUserAsync(ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        // SCIM userName is the IdP-owned, unique login identifier; reuse it as the stable
        // user id so re-provisioning is idempotent on the same key. A conflicting userName
        // is reported to the caller (SCIM 409) rather than silently overwriting.
        if (FindByUserNameInternal(provisioning.UserName) is not null)
        {
            return Task.FromResult<ManagedUser?>(null);
        }

        var now = DateTimeOffset.UtcNow;
        var user = new ManagedUser
        {
            UserId = provisioning.UserName,
            DisplayName = string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName,
            Email = provisioning.Email,
            ProvisioningSource = "scim",
            IsActive = provisioning.Active,
            Roles = NormalizeRoles(provisioning.Roles),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Guard against a concurrent create racing on the same key.
        return Task.FromResult(_users.TryAdd(user.UserId, user) ? user : null);
    }

    public Task<ManagedUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
        => Task.FromResult(FindByUserNameInternal(userName));

    public Task<ScimUserPage> ListUsersAsync(ScimUserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<ManagedUser> matches = _users.Values
            .Where(u => string.Equals(u.ProvisioningSource, "scim", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query.UserNameEquals))
        {
            matches = matches.Where(u => u.UserId.Equals(query.UserNameEquals, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = matches.OrderBy(u => u.UserId, StringComparer.OrdinalIgnoreCase).ToList();
        var skip = Math.Max(0, query.StartIndex - 1);

        var page = ordered.Skip(skip).Take(Math.Max(0, query.Count)).ToList().AsReadOnly();
        return Task.FromResult(new ScimUserPage { Users = page, TotalCount = ordered.Count });
    }

    public Task<ManagedUser?> ReplaceUserAsync(string userId, ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        if (!_users.TryGetValue(userId, out var existing))
        {
            return Task.FromResult<ManagedUser?>(null);
        }

        var updated = new ManagedUser
        {
            UserId = existing.UserId,
            DisplayName = string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName,
            Email = provisioning.Email,
            ProvisioningSource = existing.ProvisioningSource,
            ProviderId = existing.ProviderId,
            IsActive = provisioning.Active,
            Roles = NormalizeRoles(provisioning.Roles),
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _users[userId] = updated;
        return Task.FromResult<ManagedUser?>(updated);
    }

    public Task<ManagedUser?> SetActiveAsync(string userId, bool active, CancellationToken cancellationToken = default)
    {
        if (!_users.TryGetValue(userId, out var existing))
        {
            return Task.FromResult<ManagedUser?>(null);
        }

        var updated = active
            ? Clone(existing, isActive: true, roles: existing.Roles)
            : Deactivate(existing);

        _users[userId] = updated;
        return Task.FromResult<ManagedUser?>(updated);
    }

    public Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
        => DeprovisionUserAsync(userId, cancellationToken);

    /// <summary>
    /// Adds a role to a user (used by group membership sync). No-op when the user is absent.
    /// </summary>
    internal void AddRole(string userId, string role)
    {
        if (!_users.TryGetValue(userId, out var existing))
        {
            return;
        }

        if (existing.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _users[userId] = Clone(existing, existing.IsActive, NormalizeRoles([.. existing.Roles, role]));
    }

    /// <summary>
    /// Removes a role from a user (used by group membership sync). No-op when the user is absent.
    /// </summary>
    internal void RemoveRole(string userId, string role)
    {
        if (!_users.TryGetValue(userId, out var existing))
        {
            return;
        }

        if (!existing.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _users[userId] = Clone(
            existing,
            existing.IsActive,
            existing.Roles.Where(r => !r.Equals(role, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private ManagedUser? FindByUserNameInternal(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        // userId == userName for SCIM-provisioned users; fall back to a scan so OIDC/manual
        // users (whose id is a subject claim) are still matched on display identity.
        if (_users.TryGetValue(userName, out var direct))
        {
            return direct;
        }

        return _users.Values.FirstOrDefault(u => u.UserId.Equals(userName, StringComparison.OrdinalIgnoreCase));
    }

    private static ManagedUser Deactivate(ManagedUser existing) => Clone(existing, isActive: false, roles: []);

    private static ManagedUser Clone(ManagedUser existing, bool isActive, IReadOnlyList<string> roles) => new()
    {
        UserId = existing.UserId,
        DisplayName = existing.DisplayName,
        Email = existing.Email,
        ProvisioningSource = existing.ProvisioningSource,
        ProviderId = existing.ProviderId,
        IsActive = isActive,
        Roles = roles,
        CreatedAt = existing.CreatedAt,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static List<string> NormalizeRoles(IReadOnlyList<string> roles)
        => roles
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
