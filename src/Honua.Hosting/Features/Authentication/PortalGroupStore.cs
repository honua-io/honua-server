// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// In-memory Portal community-group store (#1868). Backs the ArcGIS
/// <c>community/createGroup</c> / <c>groups/{id}</c> / <c>addUsers</c> /
/// <c>removeUsers</c> surface so org/portal estates can model group membership and
/// share content to groups.
/// </summary>
/// <remarks>
/// In-memory to mirror the established <see cref="IOAuthClientStore"/> /
/// <c>IAdminApiKeyStore</c> pattern — no parallel durable store (ADR-0049). A
/// future increment may back it with the durable store behind this same interface
/// without changing callers. Identity is consumed from the shared
/// principal/role model; this store owns only group membership, not RBAC roles.
/// </remarks>
internal interface IPortalGroupStore
{
    /// <summary>Lists every group (used by group search / admin surfaces).</summary>
    Task<IReadOnlyList<PortalGroupRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Fetches a group by id, or <see langword="null"/>.</summary>
    Task<PortalGroupRecord?> GetAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>Creates a group owned by <paramref name="owner"/>; the owner is the first member.</summary>
    Task<PortalGroupRecord> CreateAsync(PortalGroupRegistration registration, string owner, CancellationToken cancellationToken);

    /// <summary>Deletes a group. Returns the removed record, or <see langword="null"/>.</summary>
    Task<PortalGroupRecord?> DeleteAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>Adds users to a group (idempotent). Returns the updated record, or null when absent.</summary>
    Task<PortalGroupRecord?> AddUsersAsync(string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken);

    /// <summary>Removes users from a group (idempotent). Returns the updated record, or null when absent.</summary>
    Task<PortalGroupRecord?> RemoveUsersAsync(string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken);

    /// <summary>Returns the ids of groups <paramref name="username"/> belongs to.</summary>
    Task<IReadOnlyList<string>> GetGroupIdsForUserAsync(string username, CancellationToken cancellationToken);
}

/// <summary>Group access tiers (ArcGIS community group <c>access</c>).</summary>
internal static class PortalGroupAccess
{
    /// <summary>Only members can see the group and its shared content.</summary>
    public const string Private = "private";

    /// <summary>Any authenticated org user can see the group.</summary>
    public const string Org = "org";

    /// <summary>Anyone can see the group.</summary>
    public const string Public = "public";
}

/// <summary>Inputs to create a community group.</summary>
internal sealed record PortalGroupRegistration(
    string Title,
    string? Description,
    string Access,
    IReadOnlyList<string> Tags);

/// <summary>A community group and its membership.</summary>
internal sealed record PortalGroupRecord(
    string Id,
    string Title,
    string? Description,
    string Owner,
    string Access,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Members,
    DateTimeOffset Created,
    DateTimeOffset Modified);

internal sealed class InMemoryPortalGroupStore(TimeProvider? timeProvider = null) : IPortalGroupStore
{
    private readonly ConcurrentDictionary<string, PortalGroupRecord> _groups =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<IReadOnlyList<PortalGroupRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PortalGroupRecord> result = _groups.Values
            .OrderBy(static g => g.Created)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<PortalGroupRecord?> GetAsync(string groupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return Task.FromResult<PortalGroupRecord?>(null);
        }

        _groups.TryGetValue(groupId, out var record);
        return Task.FromResult(record);
    }

    public Task<PortalGroupRecord> CreateAsync(PortalGroupRegistration registration, string owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var id = GenerateGroupId();
        var record = new PortalGroupRecord(
            Id: id,
            Title: registration.Title.Trim(),
            Description: registration.Description?.Trim(),
            Owner: owner,
            Access: NormalizeAccess(registration.Access),
            Tags: NormalizeList(registration.Tags),
            // The owner is implicitly a member so they can see group-shared content.
            Members: [owner],
            Created: now,
            Modified: now);

        if (!_groups.TryAdd(id, record))
        {
            throw new InvalidOperationException("Generated duplicate group identifier.");
        }

        return Task.FromResult(record);
    }

    public Task<PortalGroupRecord?> DeleteAsync(string groupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return Task.FromResult<PortalGroupRecord?>(null);
        }

        _groups.TryRemove(groupId, out var record);
        return Task.FromResult(record);
    }

    public Task<PortalGroupRecord?> AddUsersAsync(string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
        => MutateMembersAsync(groupId, usernames, add: true, cancellationToken);

    public Task<PortalGroupRecord?> RemoveUsersAsync(string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
        => MutateMembersAsync(groupId, usernames, add: false, cancellationToken);

    public Task<IReadOnlyList<string>> GetGroupIdsForUserAsync(string username, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(username))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> result = _groups.Values
            .Where(g => g.Members.Contains(username, StringComparer.OrdinalIgnoreCase))
            .Select(g => g.Id)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    private Task<PortalGroupRecord?> MutateMembersAsync(
        string groupId,
        IReadOnlyList<string> usernames,
        bool add,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(groupId) || !_groups.TryGetValue(groupId, out var record))
        {
            return Task.FromResult<PortalGroupRecord?>(null);
        }

        var requested = NormalizeList(usernames);
        var members = new List<string>(record.Members);
        if (add)
        {
            foreach (var user in requested.Where(user => !members.Contains(user, StringComparer.OrdinalIgnoreCase)))
            {
                members.Add(user);
            }
        }
        else
        {
            // The owner cannot be removed from their own group (ArcGIS semantics).
            members.RemoveAll(m =>
                !string.Equals(m, record.Owner, StringComparison.OrdinalIgnoreCase) &&
                requested.Contains(m, StringComparer.OrdinalIgnoreCase));
        }

        var updated = record with { Members = members.AsReadOnly(), Modified = _timeProvider.GetUtcNow() };
        _groups[groupId] = updated;
        return Task.FromResult<PortalGroupRecord?>(updated);
    }

    private static string NormalizeAccess(string? access)
    {
        if (string.Equals(access, PortalGroupAccess.Public, StringComparison.OrdinalIgnoreCase))
        {
            return PortalGroupAccess.Public;
        }

        if (string.Equals(access, PortalGroupAccess.Org, StringComparison.OrdinalIgnoreCase))
        {
            return PortalGroupAccess.Org;
        }

        // Private is the safe default for an unspecified/unknown access value.
        return PortalGroupAccess.Private;
    }

    private static string[] NormalizeList(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(static v => v?.Trim() ?? string.Empty)
            .Where(static v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GenerateGroupId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
