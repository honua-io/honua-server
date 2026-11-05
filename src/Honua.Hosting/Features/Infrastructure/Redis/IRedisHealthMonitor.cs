// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Redis;

public interface IRedisHealthMonitor
{
    bool IsRedisAvailable { get; }

    bool WasRedisEverAvailable { get; }

    DateTimeOffset? LastSuccessfulContact { get; }

    DateTimeOffset? LastFailure { get; }

    int ConsecutiveFailures { get; }

    bool ShouldRetryRedis { get; }

    void RecordSuccess();

    void RecordFailure(Exception exception);

    Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default);
}
