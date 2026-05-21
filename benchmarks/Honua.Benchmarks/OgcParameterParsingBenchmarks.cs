// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using Honua.Server.Features.Protocols.Ogc.Common;

namespace Honua.Benchmarks;

/// <summary>
/// OGC API parameter parsers run on every request before any work begins.
/// Most are pure string parsing with no database/HTTP dependency, so they
/// belong in the benchmark harness rather than the load-test rig.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Ogc)]
public class OgcParameterParsingBenchmarks
{
    private const string Instant = "2025-06-15T12:30:00Z";
    private const string ClosedInterval = "2025-06-15T00:00:00Z/2025-06-16T00:00:00Z";
    private const string OpenStart = "../2025-06-15T00:00:00Z";
    private const string OpenEnd = "2025-06-15T00:00:00Z/..";

    [Benchmark(Description = "TryParseRange instant")]
    public bool ParseInstant()
        => OgcTemporalFilterParser.TryParseRange(Instant, out _, out _, out _);

    [Benchmark(Description = "TryParseRange closed interval")]
    public bool ParseClosedInterval()
        => OgcTemporalFilterParser.TryParseRange(ClosedInterval, out _, out _, out _);

    [Benchmark(Description = "TryParseRange open-start interval")]
    public bool ParseOpenStart()
        => OgcTemporalFilterParser.TryParseRange(OpenStart, out _, out _, out _);

    [Benchmark(Description = "TryParseRange open-end interval")]
    public bool ParseOpenEnd()
        => OgcTemporalFilterParser.TryParseRange(OpenEnd, out _, out _, out _);
}
