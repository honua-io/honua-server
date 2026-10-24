// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.IO;

namespace Honua.Infrastructure.Services;

/// <summary>
/// Thread-static cache for <see cref="WKBReader"/> instances.
/// Centralises the pattern previously duplicated across formatters.
/// </summary>
internal static class WkbReaderCache
{
    [ThreadStatic]
    private static WKBReader? _instance;

    /// <summary>
    /// Returns a cached <see cref="WKBReader"/> for the current thread.
    /// </summary>
    public static WKBReader Get()
    {
        _instance ??= new WKBReader();
        return _instance;
    }
}
