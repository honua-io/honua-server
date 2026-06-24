// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// In-memory Portal item-sharing overlay (#1868). Records the explicit ArcGIS
/// sharing state of a portal item — shared to <c>everyone</c> (public), to the
/// <c>org</c> (any authenticated user), and/or to a set of community groups —
/// driven by the <c>content/items/{id}/share</c> and <c>/unshare</c> endpoints.
/// </summary>
/// <remarks>
/// <para>
/// This is an <em>additive overlay</em>, not a replacement for RBAC. The shared
/// query/edit pipeline and the <c>IPortalItemProjector</c> remain the authority for
/// whether a principal may read the underlying service (ADR-0049/#1375). This store
/// only records sharing intent and answers "is this item shared with this principal"
/// for the sharing surface, mirroring the established in-memory auth-store pattern
/// (no parallel durable store).
/// </para>
/// </remarks>
internal interface IPortalItemSharingStore
{
    /// <summary>Gets the current share state of an item (default: shared with no one).</summary>
    Task<PortalItemShareState> GetAsync(string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the recorded sharing owner of an item (the first principal to share
    /// it), or <see langword="null"/> when the item has never been shared. Used to
    /// authorize subsequent share/unshare to the owner (or an admin).
    /// </summary>
    Task<string?> GetOwnerAsync(string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a share request to an item, unioning the requested audiences with the
    /// existing share state. The first principal to share an item is recorded as its
    /// sharing owner. Returns the resulting state.
    /// </summary>
    Task<PortalItemShareState> ShareAsync(string itemId, string owner, PortalItemShareRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the requested audiences from an item's share state. Returns the
    /// resulting state.
    /// </summary>
    Task<PortalItemShareState> UnshareAsync(string itemId, PortalItemShareRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Returns whether <paramref name="state"/> makes the item visible to a principal
    /// who is authenticated (<paramref name="isAuthenticated"/>) and a member of
    /// <paramref name="memberGroupIds"/>.
    /// </summary>
    bool IsVisibleTo(PortalItemShareState state, bool isAuthenticated, IReadOnlyCollection<string> memberGroupIds);
}

/// <summary>A share/unshare audience request.</summary>
internal sealed record PortalItemShareRequest(
    bool Everyone,
    bool Org,
    IReadOnlyList<string> GroupIds);

/// <summary>The recorded share state of a portal item.</summary>
internal sealed record PortalItemShareState(
    bool Everyone,
    bool Org,
    IReadOnlyList<string> GroupIds)
{
    /// <summary>The default state: shared with no audience.</summary>
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
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Task.FromResult(PortalItemShareState.None);
        }

        return Task.FromResult(_shares.TryGetValue(itemId, out var state) ? state : PortalItemShareState.None);
    }

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
                foreach (var group in NormalizeGroups(request.GroupIds))
                {
                    if (!groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                    {
                        groups.Add(group);
                    }
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
