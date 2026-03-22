// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory user store for the admin API surface.
/// Will be replaced by a persistent implementation when #496/#498 land.
/// </summary>
internal sealed class InMemoryUserStore : IUserStore
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
            query = query.Where(u => u.IsActive == filter.IsActive.Value);
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
            Roles = roles,
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

        var deprovisioned = new ManagedUser
        {
            UserId = existing.UserId,
            DisplayName = existing.DisplayName,
            Email = existing.Email,
            ProvisioningSource = existing.ProvisioningSource,
            ProviderId = existing.ProviderId,
            IsActive = false,
            Roles = [],
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _users[userId] = deprovisioned;
        return Task.FromResult(true);
    }
}
