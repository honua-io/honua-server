// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal sealed class SkipCatalogOutputCacheMetadata
{
    internal static SkipCatalogOutputCacheMetadata Instance { get; } = new();

    private SkipCatalogOutputCacheMetadata()
    {
    }
}
