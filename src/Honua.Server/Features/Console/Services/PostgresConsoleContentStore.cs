// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Configuration;

namespace Honua.Server.Features.Console.Services;

// Authored Console content in the system database, so every task serves the same items and they survive a restart.
// Visibility decides who may read an item, so a change advances the shared configuration generation (ADR-0086).
internal sealed class PostgresConsoleContentStore(ConsoleStateStorage storage) : IAuthoredConsoleContent
{
    // An item's share and open-data state is keyed by its id, and an id can be chosen again on create, so that
    // state is deleted with the item: a new item must not inherit the public links or publication of an old one.
    private static readonly string[] _dependentKinds =
        [PostgresConsoleShareStore.Kind, PostgresConsoleOpenDataStore.PageKind, PostgresConsoleOpenDataStore.PublicationKind];

    private readonly ConfigurationCollectionStore<ConsoleContentItem> _items = new(
        storage.Documents, new("console-content", ConsoleJsonContext.Default.ConsoleContentItem), storage.Requests);

    public async Task<ConsoleContentListResult> ListAsync(ConsoleContentQuery query, CancellationToken cancellationToken = default)
        => ConsoleContentSelection.Page(await SnapshotItemsAsync(cancellationToken).ConfigureAwait(false), query);

    public async ValueTask<IReadOnlyCollection<ConsoleContentItem>> SnapshotItemsAsync(CancellationToken cancellationToken)
        => [.. (await _items.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false)).Values];

    public async Task<ConsoleContentItem?> GetAsync(string id, CancellationToken cancellationToken = default)
        => (await _items.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(id);

    public async Task<ConsoleContentItem> CreateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default)
    {
        var now = storage.Time.GetUtcNow();
        var id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
        var stored = item with
        {
            Id = id,
            CreatedAt = item.CreatedAt ?? now,
            UpdatedAt = now,
            Generation = 1,
            Actions = [],
        };
        if (!await _items.TryCreateAsync(id, stored, cancellationToken).ConfigureAwait(false))
        {
            throw ConsoleContentIdConflictException.AlreadyExists(id);
        }
        return stored;
    }

    public Task<ConsoleContentItem?> UpdateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default)
    {
        var now = storage.Time.GetUtcNow();
        return _items.MutateAsync(item.Id, existing =>
        {
            if (existing is null)
            {
                return null;
            }
            if (item.Generation is { } requested && existing.Generation is { } current && requested != current)
            {
                throw new InvalidOperationException("Stale generation; refresh and retry.");
            }
            return item with
            {
                Id = existing.Id,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
                Generation = (existing.Generation ?? 0) + 1,
                Actions = [],
            };
        }, cancellationToken);
    }

    public Task<ConsoleContentItem?> PatchAsync(string id, ConsoleContentItemPatch patch, CancellationToken cancellationToken = default)
    {
        var now = storage.Time.GetUtcNow();
        return _items.MutateAsync(id, existing =>
        {
            if (existing is null)
            {
                return null;
            }
            var updated = existing with
            {
                Title = patch.Title ?? existing.Title,
                Description = patch.Description ?? existing.Description,
                Tags = patch.Tags ?? existing.Tags,
                Labels = patch.Labels ?? existing.Labels,
                Visibility = patch.Visibility ?? existing.Visibility,
                UpdatedById = patch.UpdatedById ?? existing.UpdatedById,
                UpdatedAt = now,
                Actions = [],
            };
            // The patch cannot carry a team scope, so the check runs against the stored item inside the write.
            if (updated.Visibility == ConsoleVisibility.Team && string.IsNullOrWhiteSpace(updated.TeamScopeId))
            {
                throw new InvalidOperationException(
                    "visibility 'team' requires a non-empty teamScopeId; use PUT to set the team scope before patching visibility.");
            }
            return updated;
        }, cancellationToken);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        => _items.DeleteWithDependentsAsync(id, _dependentKinds, cancellationToken);

    public async Task<IReadOnlyList<ConsoleContentItem>> GetProvenanceChainAsync(
        string id, int maxDepth, CancellationToken cancellationToken = default)
        => ConsoleProvenanceWalk.Collect(
            await _items.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false), id, maxDepth);
}
