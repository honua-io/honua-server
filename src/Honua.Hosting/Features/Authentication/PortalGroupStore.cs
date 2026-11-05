// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Honua.Infrastructure.Authentication;

internal interface IPortalGroupStore
{
    IReadOnlyList<string> GetGroupIdsForUser(string username, string? tenantId = null);

    Task<IReadOnlyList<PortalGroupRecord>> ListAsync(CancellationToken cancellationToken);

    Task<PortalGroupRecord?> GetAsync(string groupId, CancellationToken cancellationToken);

    Task<PortalGroupRecord> CreateAsync(PortalGroupRegistration registration, string owner, CancellationToken cancellationToken);

    Task<PortalGroupRecord?> DeleteAsync(string groupId, CancellationToken cancellationToken);

    Task<PortalGroupRecord?> AddUsersAsync(string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken);

    Task<PortalGroupRecord?> RemoveUsersAsync(string groupId, IReadOnlyList<string> usernames, CancellationToken cancellationToken);

}

internal static class PortalGroupAccess
{
    public const string Private = "private";

    public const string Org = "org";

    public const string Public = "public";
}

internal sealed record PortalGroupRegistration(
    string Title,
    string? Description,
    string Access,
    IReadOnlyList<string> Tags,
    string? TenantId = null);

internal sealed record PortalGroupRecord(
    string Id,
    string Title,
    string? Description,
    string Owner,
    string Access,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Members,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    string? TenantId = null);

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
            Modified: now,
            TenantId: registration.TenantId);

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

    public IReadOnlyList<string> GetGroupIdsForUser(string username, string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        return _groups.Values
            .Where(g => string.Equals(g.TenantId, tenantId, StringComparison.Ordinal)
                && (string.Equals(g.Owner, username, StringComparison.OrdinalIgnoreCase)
                    || g.Members.Contains(username, StringComparer.OrdinalIgnoreCase)))
            .Select(g => g.Id)
            .ToList()
            .AsReadOnly();
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
