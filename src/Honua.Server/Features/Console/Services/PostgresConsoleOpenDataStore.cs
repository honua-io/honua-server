// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Infrastructure.Configuration;

namespace Honua.Server.Features.Console.Services;

// Open-data pages and STAC publication state in the system database, under the shared configuration
// generation (ADR-0086): what one task publishes or withdraws, every task serves on its next request.
internal sealed class PostgresConsoleOpenDataStore(
    PostgresConfigurationDocuments documents, TimeProvider timeProvider, IHttpContextAccessor? contexts = null)
    : IConsoleOpenDataStore
{
    private readonly ConfigurationCollectionStore<ConsoleOpenDataPage> _pages = new(
        documents, new("console-open-data-page", ConsoleJsonContext.Default.ConsoleOpenDataPage), contexts);
    private readonly ConfigurationCollectionStore<ConsoleStacPublicationState> _publications = new(
        documents, new("console-stac-publication", ConsoleJsonContext.Default.ConsoleStacPublicationState), contexts);

    public async Task<ConsoleOpenDataPage?> GetPageAsync(string itemId, CancellationToken cancellationToken = default)
        => (await _pages.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(itemId);

    public async Task<ConsoleOpenDataPage> SavePageAsync(ConsoleOpenDataPage page, string? principalId, CancellationToken cancellationToken = default)
    {
        var stored = page with { UpdatedAt = timeProvider.GetUtcNow(), UpdatedById = principalId };
        await _pages.MutateAsync(page.ItemId, _ => stored, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<ConsoleStacPublicationState> GetStacPublicationAsync(string itemId, CancellationToken cancellationToken = default)
        => (await _publications.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(itemId)
            ?? new ConsoleStacPublicationState { ItemId = itemId };

    public async Task<ConsoleStacPublicationState> PublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var published = await _publications.MutateAsync(itemId, existing => new ConsoleStacPublicationState
        {
            ItemId = itemId,
            Status = ConsoleStacPublicationStatus.Published,
            // The collection id is assigned once, so publishing again keeps the same public address.
            CollectionId = existing?.CollectionId ?? $"honua-{itemId}",
            Revision = (existing?.Revision ?? 0) + 1,
            FirstPublishedAt = existing?.FirstPublishedAt ?? now,
            UpdatedAt = now,
            UpdatedById = principalId,
        }, cancellationToken).ConfigureAwait(false);
        return published ?? throw new InvalidOperationException("STAC publication state was not stored.");
    }

    public async Task<ConsoleStacPublicationState?> UnpublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        ConsoleStacPublicationState? withdrawn = null;
        await _publications.MutateAsync(itemId, existing =>
        {
            withdrawn = existing is { Status: ConsoleStacPublicationStatus.Published }
                ? existing with
                {
                    Status = ConsoleStacPublicationStatus.Unpublished,
                    Revision = existing.Revision + 1,
                    UpdatedAt = now,
                    UpdatedById = principalId,
                }
                : null;
            return withdrawn ?? existing;
        }, cancellationToken).ConfigureAwait(false);
        return withdrawn;
    }

    public async Task<IReadOnlyList<string>> ListPublishedStacItemIdsAsync(CancellationToken cancellationToken = default)
        => (await _publications.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false)).Values
            .Where(static state => state.Status == ConsoleStacPublicationStatus.Published)
            .Select(static state => state.ItemId)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
