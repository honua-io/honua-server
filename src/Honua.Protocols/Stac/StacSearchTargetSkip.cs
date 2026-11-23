// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;

namespace Honua.Protocols.Stac;

// Lets a catalog-wide STAC search survive publications whose table was dropped outside Honua:
// the search skips each such publication and runs again over the remaining ones, as the
// /stac/collections listing leaves each one out (StacCollectionListing).
internal static class StacSearchTargetSkip
{
    // Claims a skip. A search never skips its last publication, so a shared cause (a dropped schema,
    // a missing routing table) that takes out every target still ends as a 500 rather than an empty
    // page. A search that names collections keeps the error: a named collection is never answered with
    // an empty result. A response with a publication left out is marked no-store, so neither the output
    // cache nor the CDN keeps it once the table is back.
    public static bool TryClaim(
        HttpContext context,
        StacSearchRequest request,
        StacV2Lookups.ResolvedStacPublication[] targets)
    {
        if (targets.Length <= 1 || request.Collections is { IsDefault: false, Length: > 0 })
        {
            return false;
        }

        context.Response.Headers.CacheControl = "no-store";
        return true;
    }

    public static StacV2Lookups.ResolvedStacPublication[] Without(
        ILogger logger,
        StacV2Lookups.ResolvedStacPublication[] targets,
        SkippedSearchTarget skipped)
    {
        var layerIndex = skipped.Target.LayerIndex;
        StacLog.SearchCollectionSkipped(logger, layerIndex.ToString(CultureInfo.InvariantCulture), skipped.Error);
        return [.. targets.Where(target => target.LayerIndex != layerIndex)];
    }

    // SQLSTATE 42P01 (undefined table) and 3F000 (invalid schema): the publication's storage is gone,
    // which retrying cannot fix. Other storage errors still fail the search. Only PostgreSQL-backed
    // stores report these codes; other providers keep failing the search.
    public static bool IsMissingStorage(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: "42P01" or "3F000" })
            {
                return true;
            }
        }

        return false;
    }
}

// A publication skipped by a catalog-wide search, with the storage error that caused it.
internal readonly record struct SkippedSearchTarget(StacV2Lookups.ResolvedStacPublication Target, Exception Error);
