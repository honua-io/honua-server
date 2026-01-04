// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Honua.Benchmarks;

/// <summary>
/// Simplified cache access benchmarks for sanity checking.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public sealed class CachingPerformanceBenchmarks
{
    [Params(1_000, 10_000)]
    public int CacheSize { get; set; }

    private Dictionary<string, byte[]> _cache = null!;
    private byte[] _payload = null!;
    private string _hitKey = string.Empty;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _payload = new byte[256];
        _cache = Enumerable.Range(0, CacheSize)
            .ToDictionary(i => $"key:{i}", _ => _payload);
        _hitKey = $"key:{CacheSize / 2}";
    }

    [Benchmark(Description = "Cache hit lookup")]
    public byte[]? CacheHit()
        => _cache.TryGetValue(_hitKey, out var value) ? value : null;

    [Benchmark(Description = "Cache miss lookup")]
    public byte[]? CacheMiss()
        => _cache.TryGetValue("missing", out var value) ? value : null;

    [Benchmark(Description = "Cache insert/update")]
    public void CacheInsert()
        => _cache[_hitKey] = _payload;
}
