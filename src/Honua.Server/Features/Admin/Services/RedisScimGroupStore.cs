// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Redis-backed SCIM 2.0 group store. Each group maps to a Honua role named after the group's
/// display name; adding or removing a member synchronizes that role onto the member's durable
/// <see cref="ManagedUser"/> record via the shared <see cref="RedisUserStore"/>, so SCIM group
/// changes handled by one replica drive RBAC role assignment — and therefore deferred-lane
/// membership revalidation (honua-server#3081) — on every replica.
/// </summary>
/// <remarks>
/// Group payload writes use optimistic compare-and-swap; the member role sync that follows is
/// applied per user through the user store's own compare-and-swap loop. The two writes are not
/// one atomic unit (Redis has no cross-key transaction here), matching the in-memory store's
/// process-local guarantees: a crash between them is repaired by the IdP's next reconciliation
/// pass.
/// </remarks>
internal sealed class RedisScimGroupStore(RedisUserStore userStore, IConnectionMultiplexer redis) : IScimGroupStore
{
    private const string GroupKeyPrefix = "iam:scim:group:";
    private const string GroupIndexKey = "iam:scim:group:all";

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<ScimGroup?> CreateGroupAsync(ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        if (await FindByDisplayNameAsync(provisioning.DisplayName, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var group = new ScimGroup
        {
            GroupId = Guid.NewGuid().ToString("D"),
            DisplayName = provisioning.DisplayName.Trim(),
            MemberUserIds = NormalizeMembers(provisioning.MemberUserIds),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var key = GetGroupKey(group.GroupId);
        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists(key));
        _ = transaction.StringSetAsync(key, Serialize(group));
        _ = transaction.SetAddAsync(GroupIndexKey, group.GroupId);
        if (!await transaction.ExecuteAsync().ConfigureAwait(false))
        {
            return null;
        }

        foreach (var userId in group.MemberUserIds)
        {
            await userStore.AddRoleAsync(userId, group.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        return group;
    }

    public async Task<ScimGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _database.StringGetAsync(GetGroupKey(groupId)).ConfigureAwait(false);
        return Deserialize(payload);
    }

    public async Task<ScimGroupPage> ListGroupsAsync(ScimGroupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<ScimGroup> matches = await ListAllAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(query.DisplayNameEquals))
        {
            matches = matches.Where(g => g.DisplayName.Equals(query.DisplayNameEquals, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = matches.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var skip = Math.Max(0, query.StartIndex - 1);
        var page = ordered.Skip(skip).Take(Math.Max(0, query.Count)).ToList().AsReadOnly();

        return new ScimGroupPage { Groups = page, TotalCount = ordered.Count };
    }

    public async Task<ScimGroup?> ReplaceGroupAsync(string groupId, ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        var result = await MutateAsync(
            groupId,
            existing => new ScimGroup
            {
                GroupId = existing.GroupId,
                DisplayName = provisioning.DisplayName.Trim(),
                MemberUserIds = NormalizeMembers(provisioning.MemberUserIds),
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken).ConfigureAwait(false);
        if (result is not { Before: { } before, After: { } after })
        {
            return null;
        }

        // A rename re-maps the role: revoke the old role from everyone, then grant the new.
        var renamed = !before.DisplayName.Equals(after.DisplayName, StringComparison.OrdinalIgnoreCase);

        foreach (var userId in before.MemberUserIds.Where(userId =>
            renamed || !after.MemberUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase)))
        {
            await userStore.RemoveRoleAsync(userId, before.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var userId in after.MemberUserIds)
        {
            await userStore.AddRoleAsync(userId, after.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        return after;
    }

    public async Task<ScimGroup?> UpdateMembersAsync(string groupId, ScimGroupMemberChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        var result = await MutateAsync(
            groupId,
            existing =>
            {
                var members = existing.MemberUserIds.ToList();
                foreach (var userId in NormalizeMembers(change.Remove))
                {
                    members.RemoveAll(m => m.Equals(userId, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var userId in NormalizeMembers(change.Add).Where(userId =>
                    !members.Contains(userId, StringComparer.OrdinalIgnoreCase)))
                {
                    members.Add(userId);
                }

                return new ScimGroup
                {
                    GroupId = existing.GroupId,
                    DisplayName = existing.DisplayName,
                    MemberUserIds = members,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            },
            cancellationToken).ConfigureAwait(false);
        if (result is not { Before: { } before, After: { } after })
        {
            return null;
        }

        foreach (var userId in before.MemberUserIds.Where(userId =>
            !after.MemberUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase)))
        {
            await userStore.RemoveRoleAsync(userId, after.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var userId in after.MemberUserIds.Where(userId =>
            !before.MemberUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase)))
        {
            await userStore.AddRoleAsync(userId, after.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        return after;
    }

    public async Task<bool> DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var removed = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            return false;
        }

        if (!await _database.KeyDeleteAsync(GetGroupKey(groupId)).ConfigureAwait(false))
        {
            return false;
        }

        await _database.SetRemoveAsync(GroupIndexKey, groupId).ConfigureAwait(false);

        foreach (var userId in removed.MemberUserIds)
        {
            await userStore.RemoveRoleAsync(userId, removed.DisplayName, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<ScimGroup?> FindByDisplayNameAsync(string displayName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var trimmed = displayName.Trim();
        var all = await ListAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(g => g.DisplayName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<ScimGroup>> ListAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = await _database.SetMembersAsync(GroupIndexKey).ConfigureAwait(false);
        var groups = new List<ScimGroup>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!id.HasValue)
            {
                continue;
            }

            var group = await GetGroupAsync(id.ToString(), cancellationToken).ConfigureAwait(false);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    /// <summary>
    /// Optimistic read-modify-write of one group record, returning the record before and after
    /// the committed mutation so the caller can compute the exact role delta to sync.
    /// </summary>
    private async Task<(ScimGroup Before, ScimGroup After)?> MutateAsync(
        string groupId,
        Func<ScimGroup, ScimGroup> mutate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        var key = GetGroupKey(groupId);
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
            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, currentPayload));
            _ = transaction.StringSetAsync(key, Serialize(updated));
            if (await transaction.ExecuteAsync().ConfigureAwait(false))
            {
                return (existing, updated);
            }
        }
    }

    private static string Serialize(ScimGroup group)
        => JsonSerializer.Serialize(group, IamStoreJsonContext.Default.ScimGroup);

    private static ScimGroup? Deserialize(RedisValue payload)
        => payload.HasValue
            ? JsonSerializer.Deserialize(payload.ToString(), IamStoreJsonContext.Default.ScimGroup)
            : null;

    private static string GetGroupKey(string groupId) => GroupKeyPrefix + groupId.Trim();

    private static List<string> NormalizeMembers(IReadOnlyList<string> members)
        => members
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .Select(static m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
