// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Redis-backed managed-user store. Backs both the admin <see cref="IUserStore"/> surface and
/// the SCIM 2.0 provisioning surface (<see cref="IScimUserStore"/>) over the same durable
/// record set, so provisioning, deprovisioning, and role changes handled by one replica are
/// authoritative on every replica and survive restarts.
/// </summary>
/// <remarks>
/// Deferred workflow firings and approval resumes revalidate submitter membership through
/// <see cref="ManagedUserPrincipalMembershipSource"/>; those lanes only exist when Redis is
/// configured (the durable workflow stores require it), so registering this store under the
/// same condition guarantees the membership answer is never a node-local snapshot of another
/// replica's writes (honua-server#3081). Mutations use optimistic compare-and-swap
/// transactions, mirroring the durable orchestration stores.
/// </remarks>
internal sealed class RedisUserStore(IConnectionMultiplexer redis) : IUserStore, IScimUserStore
{
    private const string UserKeyPrefix = "iam:user:";
    private const string UserIndexKey = "iam:user:all";
    private const string ExternalIdKeyPrefix = "iam:user:ext:";

    private readonly IDatabase _database = redis.GetDatabase();

    // ---- IUserStore ------------------------------------------------------------------

    public async Task<UserListResult> ListUsersAsync(UserListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var users = (await ListAllAsync(cancellationToken).ConfigureAwait(false)).AsEnumerable();

        if (!string.IsNullOrEmpty(filter.ProvisioningSource))
        {
            users = users.Where(u => u.ProvisioningSource.Equals(filter.ProvisioningSource, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(filter.Role))
        {
            users = users.Where(u => u.Roles.Contains(filter.Role, StringComparer.OrdinalIgnoreCase));
        }

        if (filter.IsActive.HasValue)
        {
            var isActive = filter.IsActive.Value;
            users = users.Where(u => u.IsActive == isActive);
        }

        var all = users.OrderBy(u => u.UserId, StringComparer.OrdinalIgnoreCase).ToList();

        return new UserListResult
        {
            TotalCount = all.Count,
            Users = all.Skip(filter.Offset).Take(filter.Limit).ToList().AsReadOnly(),
        };
    }

    public async Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _database.StringGetAsync(GetUserKey(userId)).ConfigureAwait(false);
        return Deserialize(payload);
    }

    public async Task<ManagedUser?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mappedUserId = await _database.StringGetAsync(GetExternalIdKey(externalId)).ConfigureAwait(false);
        if (!mappedUserId.HasValue)
        {
            return null;
        }

        // Verify the mapping against the record itself: a stale index entry (e.g. an
        // externalId later re-pointed by a SCIM PUT) must never resolve authorization
        // identity for the wrong user (honua-server#3081).
        var user = await GetUserAsync(mappedUserId.ToString(), cancellationToken).ConfigureAwait(false);
        return user is not null
            && user.ExternalId is not null
            && user.ExternalId.Equals(externalId.Trim(), StringComparison.OrdinalIgnoreCase)
                ? user
                : null;
    }

    public Task<ManagedUser?> UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return MutateAsync(userId, existing => Clone(existing, existing.IsActive, NormalizeRoles(roles)), cancellationToken);
    }

    public async Task<bool> DeprovisionUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var updated = await MutateAsync(userId, Deactivate, cancellationToken).ConfigureAwait(false);
        return updated is not null;
    }

    // ---- IScimUserStore --------------------------------------------------------------

    public async Task<ManagedUser?> CreateUserAsync(ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var user = new ManagedUser
        {
            UserId = provisioning.UserName,
            ExternalId = provisioning.ExternalId,
            DisplayName = string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName,
            Email = provisioning.Email,
            ProvisioningSource = "scim",
            IsActive = provisioning.Active,
            Roles = NormalizeRoles(provisioning.Roles),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // SET NX inside one transaction claims the userName key — and the externalId
        // mapping when supplied — atomically across replicas, so a concurrent create on
        // another node surfaces as the documented SCIM 409 rather than a silent overwrite.
        var userKey = GetUserKey(user.UserId);
        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists(userKey));
        _ = transaction.StringSetAsync(userKey, Serialize(user));
        _ = transaction.SetAddAsync(UserIndexKey, NormalizeKeyComponent(user.UserId));
        if (user.ExternalId is not null)
        {
            transaction.AddCondition(Condition.KeyNotExists(GetExternalIdKey(user.ExternalId)));
            _ = transaction.StringSetAsync(GetExternalIdKey(user.ExternalId), NormalizeKeyComponent(user.UserId));
        }

        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        return committed ? user : null;
    }

    public Task<ManagedUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
        // userId == userName for SCIM-provisioned users, and the key is case-normalized, so
        // the direct lookup is the complete case-insensitive userName match.
        => GetUserAsync(userName, cancellationToken);

    public async Task<ScimUserPage> ListUsersAsync(ScimUserQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<ManagedUser> matches = (await ListAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(u => string.Equals(u.ProvisioningSource, "scim", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query.UserNameEquals))
        {
            matches = matches.Where(u => u.UserId.Equals(query.UserNameEquals, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = matches.OrderBy(u => u.UserId, StringComparer.OrdinalIgnoreCase).ToList();
        var skip = Math.Max(0, query.StartIndex - 1);

        var page = ordered.Skip(skip).Take(Math.Max(0, query.Count)).ToList().AsReadOnly();
        return new ScimUserPage { Users = page, TotalCount = ordered.Count };
    }

    public Task<ManagedUser?> ReplaceUserAsync(string userId, ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);
        return MutateAsync(
            userId,
            existing => new ManagedUser
            {
                UserId = existing.UserId,
                ExternalId = provisioning.ExternalId ?? existing.ExternalId,
                DisplayName = string.IsNullOrWhiteSpace(provisioning.DisplayName) ? provisioning.UserName : provisioning.DisplayName,
                Email = provisioning.Email,
                ProvisioningSource = existing.ProvisioningSource,
                ProviderId = existing.ProviderId,
                IsActive = provisioning.Active,
                Roles = NormalizeRoles(provisioning.Roles),
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);
    }

    public Task<ManagedUser?> SetActiveAsync(string userId, bool active, CancellationToken cancellationToken = default)
        => MutateAsync(
            userId,
            existing => active ? Clone(existing, isActive: true, roles: existing.Roles) : Deactivate(existing),
            cancellationToken);

    public Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
        => DeprovisionUserAsync(userId, cancellationToken);

    // ---- Group role sync -------------------------------------------------------------

    /// <summary>
    /// Adds a role to a user (used by SCIM group membership sync). No-op when the user is
    /// absent.
    /// </summary>
    internal Task AddRoleAsync(string userId, string role, CancellationToken cancellationToken = default)
        => MutateAsync(
            userId,
            existing => existing.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)
                ? existing
                : Clone(existing, existing.IsActive, NormalizeRoles([.. existing.Roles, role])),
            cancellationToken);

    /// <summary>
    /// Removes a role from a user (used by SCIM group membership sync). No-op when the user
    /// is absent.
    /// </summary>
    internal Task RemoveRoleAsync(string userId, string role, CancellationToken cancellationToken = default)
        => MutateAsync(
            userId,
            existing => existing.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)
                ? Clone(
                    existing,
                    existing.IsActive,
                    existing.Roles.Where(r => !r.Equals(role, StringComparison.OrdinalIgnoreCase)).ToList())
                : existing,
            cancellationToken);

    // ---- Internals -------------------------------------------------------------------

    private async Task<IReadOnlyList<ManagedUser>> ListAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _database.SetMembersAsync(UserIndexKey).ConfigureAwait(false);
        var users = new List<ManagedUser>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var user = await GetUserAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (user is not null)
            {
                users.Add(user);
            }
        }

        return users;
    }

    /// <summary>
    /// Optimistic read-modify-write of one user record: the write commits only while the
    /// record still equals the payload that was read, so concurrent replicas (e.g. group role
    /// sync racing a SCIM PATCH) retry instead of losing updates.
    /// </summary>
    private async Task<ManagedUser?> MutateAsync(
        string userId,
        Func<ManagedUser, ManagedUser> mutate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var key = GetUserKey(userId);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentPayload = await _database.StringGetAsync(key).ConfigureAwait(false);
            var existing = Deserialize(currentPayload);
            if (existing is null)
            {
                return null;
            }

            var updated = mutate(existing);
            if (ReferenceEquals(updated, existing))
            {
                return existing;
            }

            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, currentPayload));
            _ = transaction.StringSetAsync(key, Serialize(updated));
            if (await transaction.ExecuteAsync().ConfigureAwait(false))
            {
                await SyncExternalIdIndexAsync(existing, updated).ConfigureAwait(false);
                return updated;
            }
        }
    }

    private async Task SyncExternalIdIndexAsync(ManagedUser before, ManagedUser after)
    {
        if (string.Equals(before.ExternalId, after.ExternalId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedUserId = NormalizeKeyComponent(after.UserId);
        if (before.ExternalId is not null)
        {
            // Only release a mapping this user actually owns; never disturb another user's.
            var mapped = await _database.StringGetAsync(GetExternalIdKey(before.ExternalId)).ConfigureAwait(false);
            if (mapped.HasValue && string.Equals(mapped.ToString(), normalizedUserId, StringComparison.Ordinal))
            {
                await _database.KeyDeleteAsync(GetExternalIdKey(before.ExternalId)).ConfigureAwait(false);
            }
        }

        if (after.ExternalId is not null)
        {
            // Best-effort NX claim: if another user already owns the identifier the record
            // keeps its value but lookups keep resolving the owner, and
            // FindByExternalIdAsync verifies against the record so a stale mapping can
            // never resolve the wrong user.
            await _database.StringSetAsync(
                GetExternalIdKey(after.ExternalId),
                normalizedUserId,
                when: When.NotExists).ConfigureAwait(false);
        }
    }

    private static ManagedUser Deactivate(ManagedUser existing) => Clone(existing, isActive: false, roles: []);

    private static ManagedUser Clone(ManagedUser existing, bool isActive, IReadOnlyList<string> roles) => new()
    {
        UserId = existing.UserId,
        ExternalId = existing.ExternalId,
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

    private static string Serialize(ManagedUser user)
        => JsonSerializer.Serialize(user, IamStoreJsonContext.Default.ManagedUser);

    private static ManagedUser? Deserialize(RedisValue payload)
        => payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), IamStoreJsonContext.Default.ManagedUser)
            : null;

    private static string GetUserKey(string userId) => UserKeyPrefix + NormalizeKeyComponent(userId);

    private static string GetExternalIdKey(string externalId) => ExternalIdKeyPrefix + NormalizeKeyComponent(externalId);

    /// <summary>
    /// Case-normalizes an identifier for use in a Redis key, mirroring the in-memory store's
    /// case-insensitive dictionary semantics.
    /// </summary>
    private static string NormalizeKeyComponent(string value) => value.Trim().ToLowerInvariant();
}
