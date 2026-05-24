// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

/// <summary>
/// Persistence contract for Console content items. Implementations must keep
/// list/search responses ordered by a stable key so cursor pagination remains
/// deterministic.
/// </summary>
public interface IConsoleContentStore
{
    /// <summary>
    /// Lists items matching the supplied query.
    /// </summary>
    Task<ConsoleContentListResult> ListAsync(ConsoleContentQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an item by id, or null when no such item exists.
    /// </summary>
    Task<ConsoleContentItem?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new item. Implementations populate identity and audit fields
    /// when the caller leaves them blank.
    /// </summary>
    Task<ConsoleContentItem> CreateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an item. When <c>item.Generation</c> is supplied, implementations
    /// must reject the update unless it exactly matches the stored generation
    /// (older <em>and</em> newer values are both rejected). A null generation
    /// bypasses the check (last-write-wins).
    /// </summary>
    Task<ConsoleContentItem?> UpdateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a patch to displayable fields without touching <c>Generation</c>.
    /// Implementations must enforce the team-scope invariant atomically against
    /// the latest stored snapshot: if applying the patch would leave the item
    /// with <c>Visibility == Team</c> and an empty <c>TeamScopeId</c>, the
    /// implementation must throw <see cref="InvalidOperationException"/> rather
    /// than commit. The patch contract intentionally excludes
    /// <c>TeamScopeId</c>, so the only way to enter that state is a TOCTOU race
    /// against a concurrent PUT that cleared the scope between an endpoint
    /// pre-check and the swap.
    /// </summary>
    Task<ConsoleContentItem?> PatchAsync(string id, ConsoleContentItemPatch patch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an item. Returns false when no such item existed.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the transitive provenance chain for an item, capped at the
    /// supplied depth.
    /// </summary>
    Task<IReadOnlyList<ConsoleContentItem>> GetProvenanceChainAsync(string id, int maxDepth, CancellationToken cancellationToken = default);
}

/// <summary>
/// Partial update for displayable item fields. Null members are ignored.
/// </summary>
public sealed record ConsoleContentItemPatch
{
    /// <summary>New display title.</summary>
    public string? Title { get; init; }

    /// <summary>New description.</summary>
    public string? Description { get; init; }

    /// <summary>Replacement tag list.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Replacement label map.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>New visibility scope.</summary>
    public ConsoleVisibility? Visibility { get; init; }

    /// <summary>Identifier of the principal performing the patch (audit).</summary>
    public string? UpdatedById { get; init; }
}
