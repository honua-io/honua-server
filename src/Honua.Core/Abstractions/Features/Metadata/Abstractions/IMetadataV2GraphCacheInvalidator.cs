// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

// Read-modify-write uses the uncached store, because the cache is only a read snapshot.
public interface IMetadataV2GraphCacheInvalidator
{
    void Invalidate(string environment);

    void InvalidateAll();
}
