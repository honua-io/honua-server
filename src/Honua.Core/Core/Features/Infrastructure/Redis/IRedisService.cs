// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Redis;

public interface IRedisService
{
    bool IsUsingRedis { get; }

    bool IsRedisConfigured { get; }

    Task<bool> TryRestoreRedisAsync(CancellationToken cancellationToken = default);
}
