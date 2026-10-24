// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal interface ICacheStorageMetricsProvider
{
    Task<CacheStorageMetrics> GetStorageMetricsAsync(CancellationToken cancellationToken = default);
}

internal sealed record CacheStorageMetrics
{
    public required string Backend { get; init; }
    public required bool IsUsingFallback { get; init; }
    public required int LocalKeyCount { get; init; }
    public long? DistributedKeyCount { get; init; }
    public long? RedisDatabaseSize { get; init; }
    public long? RedisUsedMemoryBytes { get; init; }
    public string? RedisUsedMemoryHuman { get; init; }
}
