// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Core.Features.Studio.Services;

public sealed class StoreSharedStyleConsumerIndex : ISharedStyleConsumerIndex
{
    private const int PageSize = 200;

    private readonly IStudioPackageStore _store;

    public StoreSharedStyleConsumerIndex(IStudioPackageStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<SharedStyleConsumerScan> ListConsumersAsync(
        SharedStyleConsumerQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!SharedStyleReferences.CanMatchLiterally(query.StyleId) || query.Limit < 1)
        {
            return new SharedStyleConsumerScan { Consumers = [], Complete = false };
        }

        var bucket = new Bucket(query.StyleId, query.Limit, query.Tenant);
        await CollectItemsAsync(bucket, cancellationToken).ConfigureAwait(false);
        await CollectDraftsAsync(bucket, cancellationToken).ConfigureAwait(false);
        return bucket.ToScan();
    }

    private async Task CollectItemsAsync(Bucket bucket, CancellationToken cancellationToken)
    {
        var page = await _store.ListContentItemsAsync(new StudioContentItemQuery
        {
            Families = [StudioPackageFamily.Map, StudioPackageFamily.App],
            Tenant = bucket.Tenant,
            Limit = PageSize
        }, cancellationToken).ConfigureAwait(false);
        if (page.NextCursor is not null || page.Total > page.Items.Count)
        {
            bucket.MarkPartial();
            return;
        }

        foreach (var item in page.Items)
        {
            if (!bucket.HasRoom)
            {
                bucket.MarkPartial();
                return;
            }

            if (item.CurrentVersionId is Guid current)
            {
                await AddVersionAsync(bucket, CurrentVersion(item, current), cancellationToken).ConfigureAwait(false);
            }

            if (item.PublishedVersionId is Guid published && published != item.CurrentVersionId)
            {
                await AddVersionAsync(bucket, SavedVersion(item, published), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CollectDraftsAsync(Bucket bucket, CancellationToken cancellationToken)
    {
        var page = await _store.ListDraftsAsync(new StudioPackageDraftQuery
        {
            Families = [StudioPackageFamily.Map, StudioPackageFamily.App],
            Tenant = bucket.Tenant,
            Limit = PageSize
        }, cancellationToken).ConfigureAwait(false);
        if (page.NextCursor is not null || page.Total > page.Items.Count)
        {
            bucket.MarkPartial();
            return;
        }

        foreach (var draft in page.Items)
        {
            if (!bucket.HasRoom)
            {
                bucket.MarkPartial();
                return;
            }

            AddDraft(bucket, draft);
        }
    }

    private static VersionRequest CurrentVersion(StudioContentItemSummary item, Guid versionId) =>
        new() { Item = item, VersionId = versionId, Current = true };

    private static VersionRequest SavedVersion(StudioContentItemSummary item, Guid versionId) =>
        new() { Item = item, VersionId = versionId, Current = false };

    private async Task AddVersionAsync(Bucket bucket, VersionRequest request, CancellationToken cancellationToken)
    {
        var version = await _store.GetVersionAsync(request.Item.ItemId, request.VersionId, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(version.Envelope, StudioJsonContext.Default.StudioPackageEnvelope);
        if (!SharedStyleReferences.AppearsIn(json, bucket.StyleId))
        {
            return;
        }

        bucket.Add(new SharedStyleConsumerRow
        {
            Kind = request.Item.Family.ToString().ToLowerInvariant(),
            ItemId = request.Item.ItemId.ToString("D"),
            PackageKey = request.Item.PackageKey,
            ContentHash = version.ContentHash,
            PublicationState = request.Item.PublishedVersionId == request.VersionId ? "published" : "saved",
            VersionId = request.VersionId.ToString("D"),
            Generation = version.VersionNumber,
            IsCurrent = request.Current
        });
    }

    private static void AddDraft(Bucket bucket, StudioPackageDraft draft)
    {
        var json = JsonSerializer.Serialize(draft.Envelope, StudioJsonContext.Default.StudioPackageEnvelope);
        if (!SharedStyleReferences.AppearsIn(json, bucket.StyleId))
        {
            return;
        }

        bucket.Add(new SharedStyleConsumerRow
        {
            Kind = draft.Family.ToString().ToLowerInvariant(),
            ItemId = draft.ItemId.ToString("D"),
            PackageKey = draft.PackageKey,
            ContentHash = SharedStyleIdentity.ContentHash(json),
            PublicationState = "draft",
            DraftId = draft.DraftId.ToString("D"),
            Generation = draft.Generation,
            IsCurrent = true
        });
    }

    private sealed class VersionRequest
    {
        public required StudioContentItemSummary Item { get; init; }

        public required Guid VersionId { get; init; }

        public required bool Current { get; init; }
    }

    private sealed class Bucket
    {
        private readonly List<SharedStyleConsumerRow> _consumers = [];
        private bool _complete = true;

        public Bucket(string styleId, int limit, TenantScopeFilter? tenant)
        {
            StyleId = styleId;
            Limit = limit;
            Tenant = tenant;
        }

        public string StyleId { get; }

        public TenantScopeFilter? Tenant { get; }

        public int Limit { get; }

        public bool HasRoom => _consumers.Count <= Limit;

        public void MarkPartial() => _complete = false;

        public void Add(SharedStyleConsumerRow row) => _consumers.Add(row);

        public SharedStyleConsumerScan ToScan()
        {
            var complete = _complete && _consumers.Count <= Limit;
            if (_consumers.Count > Limit)
            {
                _consumers.RemoveRange(Limit, _consumers.Count - Limit);
            }

            return new SharedStyleConsumerScan { Consumers = _consumers, Complete = complete };
        }
    }
}
