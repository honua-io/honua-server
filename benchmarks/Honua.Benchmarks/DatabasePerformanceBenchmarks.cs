// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Honua.Core.Features.FeatureStore.Domain;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Benchmarks;

/// <summary>
/// Lightweight query construction benchmarks for the feature store.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public sealed class DatabasePerformanceBenchmarks
{
    private FeatureQuery _baseQuery;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _baseQuery = new FeatureQuery
        {
            Where = "value > 0",
            Limit = 100,
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326
        };
    }

    [Benchmark(Description = "Clone query with paging")]
    public FeatureQuery CloneQueryWithPaging()
        => _baseQuery with { Offset = 500 };

    [Benchmark(Description = "Build spatial filter")]
    [SuppressMessage("Performance", "CA1822", Justification = "BenchmarkDotNet benchmark methods are kept as instance methods for consistent discovery.")]
    public SpatialFilter BuildSpatialFilter()
    {
        var wkb = new byte[] { 1, 2, 3, 4 };
        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, 4326);
    }
}
