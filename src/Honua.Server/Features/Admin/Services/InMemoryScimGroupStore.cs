// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// In-memory SCIM 2.0 group store (#510). Each group maps to a Honua role named after the
/// group's display name; adding or removing a member synchronizes that role onto the member's
/// <see cref="ManagedUser"/> record via the shared <see cref="InMemoryUserStore"/> so SCIM
/// group changes immediately drive RBAC role assignment.
/// Will be replaced by a persistent implementation alongside the durable user store (#498).
/// </summary>
internal sealed class InMemoryScimGroupStore(InMemoryUserStore userStore) : IScimGroupStore
{
    private readonly ConcurrentDictionary<string, ScimGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly InMemoryUserStore _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
    private readonly object _sync = new();

    public Task<ScimGroup?> CreateGroupAsync(ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        lock (_sync)
        {
            if (FindByDisplayNameInternal(provisioning.DisplayName) is not null)
            {
                return Task.FromResult<ScimGroup?>(null);
            }

            var now = DateTimeOffset.UtcNow;
            var members = NormalizeMembers(provisioning.MemberUserIds);
            var group = new ScimGroup
            {
                GroupId = Guid.NewGuid().ToString("D"),
                DisplayName = provisioning.DisplayName.Trim(),
                MemberUserIds = members,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _groups[group.GroupId] = group;

            foreach (var userId in members)
            {
                _userStore.AddRole(userId, group.DisplayName);
            }

            return Task.FromResult<ScimGroup?>(group);
        }
    }

    public Task<ScimGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        _groups.TryGetValue(groupId, out var group);
        return Task.FromResult(group);
    }

    public Task<ScimGroupPage> ListGroupsAsync(ScimGroupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<ScimGroup> matches = _groups.Values;

        if (!string.IsNullOrEmpty(query.DisplayNameEquals))
        {
            matches = matches.Where(g => g.DisplayName.Equals(query.DisplayNameEquals, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = matches.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var skip = Math.Max(0, query.StartIndex - 1);
        var page = ordered.Skip(skip).Take(Math.Max(0, query.Count)).ToList().AsReadOnly();

        return Task.FromResult(new ScimGroupPage { Groups = page, TotalCount = ordered.Count });
    }

    public Task<ScimGroup?> ReplaceGroupAsync(string groupId, ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        lock (_sync)
        {
            if (!_groups.TryGetValue(groupId, out var existing))
            {
                return Task.FromResult<ScimGroup?>(null);
            }

            var newMembers = NormalizeMembers(provisioning.MemberUserIds);
            var newName = provisioning.DisplayName.Trim();

            // A rename re-maps the role: revoke the old role from everyone, then grant the new.
            var renamed = !existing.DisplayName.Equals(newName, StringComparison.OrdinalIgnoreCase);

            // Revoke the (old) role from members that are leaving or affected by a rename.
            foreach (var userId in existing.MemberUserIds.Where(userId =>
                renamed || !newMembers.Contains(userId, StringComparer.OrdinalIgnoreCase)))
            {
                _userStore.RemoveRole(userId, existing.DisplayName);
            }

            // Grant the (new) role to the resulting member set.
            foreach (var userId in newMembers)
            {
                _userStore.AddRole(userId, newName);
            }

            var updated = new ScimGroup
            {
                GroupId = existing.GroupId,
                DisplayName = newName,
                MemberUserIds = newMembers,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _groups[groupId] = updated;
            return Task.FromResult<ScimGroup?>(updated);
        }
    }

    public Task<ScimGroup?> UpdateMembersAsync(string groupId, ScimGroupMemberChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_sync)
        {
            if (!_groups.TryGetValue(groupId, out var existing))
            {
                return Task.FromResult<ScimGroup?>(null);
            }

            var members = existing.MemberUserIds.ToList();

            // Not a simple filter: members.RemoveAll(...) is itself the mutation being
            // conditioned on, so folding it into a LINQ Where predicate would hide a
            // side-effecting call inside what should read as a pure filter.
            foreach (var userId in NormalizeMembers(change.Remove))
            {
                if (members.RemoveAll(m => m.Equals(userId, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    _userStore.RemoveRole(userId, existing.DisplayName);
                }
            }

            foreach (var userId in NormalizeMembers(change.Add).Where(userId =>
                !members.Contains(userId, StringComparer.OrdinalIgnoreCase)))
            {
                members.Add(userId);
                _userStore.AddRole(userId, existing.DisplayName);
            }

            var updated = new ScimGroup
            {
                GroupId = existing.GroupId,
                DisplayName = existing.DisplayName,
                MemberUserIds = members,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _groups[groupId] = updated;
            return Task.FromResult<ScimGroup?>(updated);
        }
    }

    public Task<bool> DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_groups.TryRemove(groupId, out var removed))
            {
                return Task.FromResult(false);
            }

            foreach (var userId in removed.MemberUserIds)
            {
                _userStore.RemoveRole(userId, removed.DisplayName);
            }

            return Task.FromResult(true);
        }
    }

    private ScimGroup? FindByDisplayNameInternal(string displayName)
        => string.IsNullOrWhiteSpace(displayName)
            ? null
            : _groups.Values.FirstOrDefault(g => g.DisplayName.Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase));

    private static List<string> NormalizeMembers(IReadOnlyList<string> members)
        => members
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .Select(static m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
