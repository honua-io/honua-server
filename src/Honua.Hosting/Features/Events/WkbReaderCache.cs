// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.IO;

namespace Honua.Infrastructure.Services;

internal static class WkbReaderCache
{
    [ThreadStatic]
    private static WKBReader? _instance;

    public static WKBReader Get()
    {
        _instance ??= new WKBReader();
        return _instance;
    }
}
