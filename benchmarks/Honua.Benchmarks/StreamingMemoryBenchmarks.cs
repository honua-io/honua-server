// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Honua.Benchmarks;

/// <summary>
/// Memory-focused streaming benchmarks.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public sealed class StreamingMemoryBenchmarks : IDisposable
{
    [Params(1_000, 10_000, 100_000)]
    public int StreamSize { get; set; }

    private List<int> _data = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _data = Enumerable.Range(0, StreamSize).ToList();
    }

    [Benchmark(Description = "Sync sum over in-memory stream")]
    public int SumValues()
        => _data.Sum();

    [Benchmark(Description = "Async sum over in-memory stream")]
    public async Task<int> SumValuesAsync()
    {
        var sum = 0;
        foreach (var value in _data)
        {
            sum += await Task.FromResult(value);
        }

        return sum;
    }

    public void Dispose()
    {
        _data.Clear();
    }
}
