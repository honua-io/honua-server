// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;

namespace Honua.Protocols.Stac;

// Lets the catalog-wide /stac/collections listing survive a publication whose table or schema was
// dropped outside Honua: the listing leaves that collection out. GET /stac/collections/{id} keeps the
// error, so a named collection is never hidden, and a listing where every collection fails that way
// still fails, so a shared cause is not answered with an empty catalog. A listing with a collection
// left out is marked no-store, so neither the output cache nor the CDN keeps it once the table is back.
internal sealed class StacCollectionListing(HttpContext context, ILogger logger)
{
    private Exception? _firstMissing;

    // Maps one publication; a dropped table is logged and yields an empty entry.
    public async Task<ListedCollection> MapAsync(
        StacV2Lookups.ResolvedStacPublication resolved,
        StacMappingService.CollectionMappingContext mapping,
        CancellationToken cancellationToken)
    {
        try
        {
            return new ListedCollection(
                await StacMappingService.MapResourceToCollectionAsync(resolved, mapping, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (StacSearchTargetSkip.IsMissingStorage(ex))
        {
            Interlocked.CompareExchange(ref _firstMissing, ex, null);
            StacLog.CollectionsListingSkipped(logger, resolved.LayerIndex.ToString(CultureInfo.InvariantCulture), ex);
            return new ListedCollection(null);
        }
    }

    // The listed collections, or the first missing-storage error when every one was left out.
    public ImmutableArray<StacCollection> Complete(IEnumerable<ListedCollection> listed)
    {
        var collections = listed.Select(static entry => entry.Collection).OfType<StacCollection>().ToImmutableArray();
        if (_firstMissing is null)
        {
            return collections;
        }

        if (collections.IsEmpty)
        {
            ExceptionDispatchInfo.Throw(_firstMissing);
        }

        context.Response.Headers.CacheControl = "no-store";

        return collections;
    }

    // A reference wrapper, because the projection helper takes only non-null class results.
    internal sealed record ListedCollection(StacCollection? Collection);
}
