// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Authentication;

// Sharing is additive to RBAC: an explicit group audience is an alternative service read grant, and an everyone or org audience confers no authority.
internal interface IPortalItemSharingStore
{
    PortalItemShareState Get(string itemId);

    Task<PortalItemShareState> GetAsync(string itemId, CancellationToken cancellationToken);

    // The recorded owner is the first principal to share the item and is who may later change that share, along with an admin.
    Task<string?> GetOwnerAsync(string itemId, CancellationToken cancellationToken);

    Task<PortalItemShareState> ShareAsync(string itemId, string owner, PortalItemShareRequest request, CancellationToken cancellationToken);

    Task<PortalItemShareState> UnshareAsync(string itemId, PortalItemShareRequest request, CancellationToken cancellationToken);

    bool IsVisibleTo(PortalItemShareState state, bool isAuthenticated, IReadOnlyCollection<string> memberGroupIds);
}

internal sealed record PortalItemShareRequest(
    bool Everyone,
    bool Org,
    IReadOnlyList<string> GroupIds);

internal sealed record PortalItemShareState(
    bool Everyone,
    bool Org,
    IReadOnlyList<string> GroupIds)
{
    public static PortalItemShareState None { get; } = new(false, false, []);
}

internal sealed class InMemoryPortalItemSharingStore : IPortalItemSharingStore
{
    private readonly ConcurrentDictionary<string, PortalItemShareState> _shares =
        new(StringComparer.Ordinal);

    // The first principal to share an item is recorded as its sharing owner so the
    // endpoint can restrict subsequent share/unshare to the owner or an admin.
    private readonly ConcurrentDictionary<string, string> _owners =
        new(StringComparer.Ordinal);

    public Task<PortalItemShareState> GetAsync(string itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Get(itemId));
    }

    public PortalItemShareState Get(string itemId)
        => !string.IsNullOrWhiteSpace(itemId) && _shares.TryGetValue(itemId, out var state)
            ? state
            : PortalItemShareState.None;

    public Task<string?> GetOwnerAsync(string itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(_owners.TryGetValue(itemId, out var owner) ? owner : null);
    }

    public Task<PortalItemShareState> ShareAsync(string itemId, string owner, PortalItemShareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        cancellationToken.ThrowIfCancellationRequested();

        // First sharer wins ownership; subsequent shares do not transfer it.
        _owners.TryAdd(itemId, owner);

        var updated = _shares.AddOrUpdate(
            itemId,
            _ => new PortalItemShareState(
                request.Everyone,
                // Sharing to everyone implies org visibility too.
                request.Org || request.Everyone,
                NormalizeGroups(request.GroupIds)),
            (_, existing) =>
            {
                var groups = new List<string>(existing.GroupIds);
                foreach (var group in NormalizeGroups(request.GroupIds).Where(group => !groups.Contains(group, StringComparer.OrdinalIgnoreCase)))
                {
                    groups.Add(group);
                }

                return existing with
                {
                    Everyone = existing.Everyone || request.Everyone,
                    Org = existing.Org || request.Org || request.Everyone,
                    GroupIds = groups.AsReadOnly(),
                };
            });

        return Task.FromResult(updated);
    }

    public Task<PortalItemShareState> UnshareAsync(string itemId, PortalItemShareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_shares.TryGetValue(itemId, out var existing))
        {
            return Task.FromResult(PortalItemShareState.None);
        }

        var remove = NormalizeGroups(request.GroupIds);
        var groups = existing.GroupIds
            .Where(g => !remove.Contains(g, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var updated = existing with
        {
            // Revoking everyone also revokes org when org was only implied by everyone;
            // an explicit org share is preserved unless org is unshared too.
            Everyone = existing.Everyone && !request.Everyone,
            Org = existing.Org && !request.Org && !request.Everyone,
            GroupIds = groups.AsReadOnly(),
        };

        _shares[itemId] = updated;
        return Task.FromResult(updated);
    }

    public bool IsVisibleTo(PortalItemShareState state, bool isAuthenticated, IReadOnlyCollection<string> memberGroupIds)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Everyone)
        {
            return true;
        }

        if (state.Org && isAuthenticated)
        {
            return true;
        }

        if (state.GroupIds.Count > 0 && memberGroupIds is { Count: > 0 })
        {
            return state.GroupIds.Any(g => memberGroupIds.Contains(g, StringComparer.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string[] NormalizeGroups(IReadOnlyList<string>? values)
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
}
