// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

public interface ICacheManager
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    Task<int> RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    CacheHealthInfo GetHealthInfo();
}

public sealed record CacheHealthInfo
{
    public bool IsHealthy { get; init; }

    public long TotalKeys { get; init; }

    public long MemoryUsageBytes { get; init; }

    public double HitRatePercent { get; init; }

    public string? HealthMessage { get; init; }
}
