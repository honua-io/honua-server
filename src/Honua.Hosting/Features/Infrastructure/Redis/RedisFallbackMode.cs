// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

public enum RedisFallbackMode
{
    FailFast = 0,

    InMemoryFallback = 1,

    AllowLocalInDev = 2
}
