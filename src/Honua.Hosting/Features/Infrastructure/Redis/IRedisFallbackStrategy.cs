// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.Redis;

public interface IRedisFallbackStrategy
{
    RedisFallbackMode Mode { get; }

    bool ShouldAllowFallback(IRedisHealthMonitor healthMonitor, IHostEnvironment hostEnvironment);

    Exception GetUnavailableException(string operation, Exception? lastException = null);
}
