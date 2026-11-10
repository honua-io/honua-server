// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

internal sealed class SharedCacheLifetime
{
    public int MaxAgeSeconds { get; set; }

    public int SharedMaxAgeSeconds { get; set; }

    public int StaleWhileRevalidateSeconds { get; set; }

    internal static SharedCacheLifetime ForBrowserAndCdn(int seconds)
        => new() { MaxAgeSeconds = seconds, SharedMaxAgeSeconds = seconds };
}
