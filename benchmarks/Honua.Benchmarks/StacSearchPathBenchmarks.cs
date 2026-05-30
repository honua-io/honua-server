// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Stac.Services;

namespace Honua.Benchmarks;

/// <summary>
/// STAC hot-path microbenchmarks. These cover the pure parsing helpers that
/// every STAC search request runs before any database I/O is issued, so any
/// regression here multiplies across the entire search throughput.
/// Paths that require a live database (item mapping, layer resolution) are
/// intentionally excluded — see the integration soak tests for those.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Stac)]
public class StacSearchPathBenchmarks
{
    private const string Bbox2D = "-180,-90,180,90";
    private const string Bbox3D = "-122.5,37.7,0,-122.3,37.9,100";
    private const string BboxAntimeridian = "170,-10,-170,10";

    [Benchmark(Description = "ParseBbox 2D")]
    public SpatialFilter? ParseBbox2D() => StacFilterHelpers.ParseBbox(Bbox2D);

    [Benchmark(Description = "ParseBbox 3D (z stripped)")]
    public SpatialFilter? ParseBbox3D() => StacFilterHelpers.ParseBbox(Bbox3D);

    [Benchmark(Description = "ParseBbox antimeridian-crossing")]
    public SpatialFilter? ParseBboxAntimeridian() => StacFilterHelpers.ParseBbox(BboxAntimeridian);

    [Benchmark(Description = "CreateBboxSpatialFilter direct")]
    public SpatialFilter CreateBboxSpatialFilterDirect()
        => StacFilterHelpers.CreateBboxSpatialFilter(-122.5, 37.7, -122.3, 37.9);
}
