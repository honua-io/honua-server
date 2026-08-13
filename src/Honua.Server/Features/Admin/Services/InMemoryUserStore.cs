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
/// Default for no-database/test profiles only: on Postgres profiles the durable
/// <c>PostgresUserStore</c> registered by <c>AddPostgreSqlServices</c> takes precedence
/// (#3141), because a node-local store cannot answer managed-membership queries across
/// replicas or restarts (honua-server#3081).
/// </summary>
internal sealed class InMemoryUserStore : IUserStore, IScimUserStore
{
    private readonly ConcurrentDictionary<string, ManagedUser> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _externalIdWriteLock = new();

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
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult<ManagedUser?>(null);
        }

        if (_users.TryGetValue(userId, out var user))
        {
            return Task.FromResult<ManagedUser?>(user);
        }

        // Fall back to the stable external subject (SCIM externalId / OIDC sub) so a
        // deferred security snapshot keyed by the OIDC subject resolves the managed record
        // even when the SCIM userName differs from the subject (honua-server#3141).
        var matches = _users.Values
            .Where(user => ExternalIdEquals(user, userId, issuer: null, requireIssuerMatch: false))
            .Take(2)
            .ToArray();
        return Task.FromResult(matches.Length == 1 ? matches[0] : null);
    }

    public Task<ManagedUser?> GetUserByPrincipalIdAsync(
        string principalId,
        string? issuer = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return Task.FromResult<ManagedUser?>(null);
        }

        // Authentication snapshots are keyed by the IdP-owned subject. Resolve that
        // namespace first so an unrelated record whose userId happens to equal the
        // subject cannot contribute its roles.
        var externalUser = FindByExternalIdInternal(principalId, issuer);
        if (issuer is not null)
        {
            return Task.FromResult(externalUser);
        }

        return Task.FromResult(externalUser ??
            (_users.TryGetValue(principalId, out var directUser) ? directUser : null));
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
            ExternalId = existing.ExternalId,
            ExternalIssuer = existing.ExternalIssuer,
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
        // or externalId is reported to the caller (SCIM 409) rather than silently
        // overwriting.
        var externalId = string.IsNullOrWhiteSpace(provisioning.ExternalId) ? null : provisioning.ExternalId.Trim();
        lock (_externalIdWriteLock)
        {
            if (FindByUserNameInternal(provisioning.UserName) is not null ||
                (externalId is not null && FindByExternalIdInternal(externalId, provisioning.ExternalIssuer) is not null))
            {
                return Task.FromResult<ManagedUser?>(null);
            }

            var now = DateTimeOffset.UtcNow;
            var user = new ManagedUser
            {
                UserId = provisioning.UserName,
                ExternalId = externalId,
                ExternalIssuer = NormalizeIssuer(provisioning.ExternalIssuer),
                DisplayName = string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName,
                Email = provisioning.Email,
                ProvisioningSource = "scim",
                IsActive = provisioning.Active,
                Roles = NormalizeRoles(provisioning.Roles),
                CreatedAt = now,
                UpdatedAt = now,
            };

            // Serialize external-id validation with writes so two concurrent requests cannot
            // both claim the same issuer-scoped subject under different user names.
            return Task.FromResult(_users.TryAdd(user.UserId, user) ? user : null);
        }
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

        lock (_externalIdWriteLock)
        {
            if (!_users.TryGetValue(userId, out var existing))
            {
                return Task.FromResult<ManagedUser?>(null);
            }

            var externalId = string.IsNullOrWhiteSpace(provisioning.ExternalId)
                ? existing.ExternalId
                : provisioning.ExternalId.Trim();
            var externalIssuer = string.IsNullOrWhiteSpace(provisioning.ExternalId)
                ? existing.ExternalIssuer
                : NormalizeIssuer(provisioning.ExternalIssuer);
            if (externalId is not null && _users.Values.Any(user =>
                !user.UserId.Equals(existing.UserId, StringComparison.OrdinalIgnoreCase) &&
                ExternalIdEquals(user, externalId, externalIssuer, requireIssuerMatch: true)))
            {
                throw new InvalidOperationException($"Another user already has externalId '{externalId}'.");
            }

            var updated = new ManagedUser
            {
                UserId = existing.UserId,
                // external_id is only overwritten when the IdP supplies one: losing the stable
                // subject on a PUT that omits it would orphan in-flight deferred snapshots
                // keyed by that subject (honua-server#3141).
                ExternalId = externalId,
                ExternalIssuer = externalIssuer,
                DisplayName = string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName,
                Email = provisioning.Email,
                ProvisioningSource = existing.ProvisioningSource,
                ProviderId = existing.ProviderId,
                IsActive = provisioning.Active,
                Roles = provisioning.Active ? NormalizeRoles(provisioning.Roles) : [],
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _users[userId] = updated;
            return Task.FromResult<ManagedUser?>(updated);
        }
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

    private ManagedUser? FindByExternalIdInternal(string externalId, string? issuer)
        => string.IsNullOrWhiteSpace(externalId)
            ? null
            : _users.Values.FirstOrDefault(u =>
                ExternalIdEquals(u, externalId, issuer, requireIssuerMatch: true));

    private static bool ExternalIdEquals(
        ManagedUser user,
        string externalId,
        string? issuer,
        bool requireIssuerMatch)
        => user.ExternalId is not null
            && user.ExternalId.Equals(externalId, StringComparison.Ordinal)
            && (!requireIssuerMatch || string.Equals(user.ExternalIssuer, NormalizeIssuer(issuer), StringComparison.Ordinal));

    private static string? NormalizeIssuer(string? issuer)
        => string.IsNullOrWhiteSpace(issuer) ? null : issuer.Trim();

    private static ManagedUser Deactivate(ManagedUser existing) => Clone(existing, isActive: false, roles: []);

    private static ManagedUser Clone(ManagedUser existing, bool isActive, IReadOnlyList<string> roles) => new()
    {
        UserId = existing.UserId,
        ExternalId = existing.ExternalId,
        ExternalIssuer = existing.ExternalIssuer,
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
