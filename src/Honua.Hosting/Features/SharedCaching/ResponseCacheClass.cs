// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

// A route with no declared class is never shared-cacheable.
internal enum ResponseCacheClass
{
    Tile,
    FeatureQuery,
    MetadataDocument,
    StaticMapImage,
    StaticObject,
}

internal sealed record SharedCacheClassMetadata(ResponseCacheClass CacheClass);
