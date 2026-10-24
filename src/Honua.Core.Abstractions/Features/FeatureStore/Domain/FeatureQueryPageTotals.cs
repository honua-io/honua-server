// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Chooses the match count a paged feature query may report.
/// </summary>
public static class FeatureQueryPageTotals
{
    /// <summary>
    /// Uses the page length only when that page is the complete match set.
    /// A truncated page or a later page reports <paramref name="exactMatchCount"/>.
    /// </summary>
    /// <param name="returnedCount">Rows in the trimmed page.</param>
    /// <param name="hasMore">True when another row exists past the page.</param>
    /// <param name="offset">Rows skipped before this page.</param>
    /// <param name="exactMatchCount">Count of matching rows, ignoring the page limit.</param>
    /// <returns>The total count safe to advertise as numberMatched.</returns>
    public static long ResolveReportedTotalCount(int returnedCount, bool hasMore, int offset, long exactMatchCount)
        => !hasMore && offset == 0 ? returnedCount : exactMatchCount;
}
