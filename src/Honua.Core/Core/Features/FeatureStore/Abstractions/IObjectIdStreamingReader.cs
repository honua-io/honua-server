// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

// Object ids in query order, read one row at a time so an unbounded returnIdsOnly never holds the full id set.
public interface IObjectIdStreamingReader
{
    IAsyncEnumerable<long> StreamObjectIdsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
