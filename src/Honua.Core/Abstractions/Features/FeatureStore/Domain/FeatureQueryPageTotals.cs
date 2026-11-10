// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public static class FeatureQueryPageTotals
{
    public static long ResolveReportedTotalCount(int returnedCount, bool hasMore, int offset, long exactMatchCount)
        => !hasMore && offset == 0 ? returnedCount : exactMatchCount;
}
