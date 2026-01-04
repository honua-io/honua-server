// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Honua.Benchmarks;

/// <summary>
/// Simplified concurrency benchmarks for task fan-out behavior.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public sealed class LoadTestConcurrencyBenchmarks
{
    [Params(10, 50, 100)]
    public int ConcurrentUsers { get; set; }

    [Benchmark(Description = "Concurrent task fan-out")]
    public async Task<int> ConcurrentWork()
    {
        var tasks = Enumerable.Range(0, ConcurrentUsers)
            .Select(_ => Task.Run(() => 1));
        var results = await Task.WhenAll(tasks);
        return results.Sum();
    }
}
