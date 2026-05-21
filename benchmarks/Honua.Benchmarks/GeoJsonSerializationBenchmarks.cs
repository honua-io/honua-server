// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;

namespace Honua.Benchmarks;

/// <summary>
/// OGC API Features serialises <see cref="FeatureCollection"/> on every
/// <c>/items</c> response via the source-generated <c>OgcJsonContext</c>.
/// Throughput is dominated by JSON writer cost and dictionary-property
/// boxing, both of which are pure CPU and safe to microbenchmark.
/// We sweep small (10) / medium (1k) / large (10k) feature counts so any
/// per-feature allocation regression is visible across the relevant scale.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Ogc, Categories.Serialization)]
public class GeoJsonSerializationBenchmarks
{
    private FeatureCollection _smallCollection = null!;
    private FeatureCollection _mediumCollection = null!;
    private FeatureCollection _largeCollection = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallCollection = BuildCollection(featureCount: 10);
        _mediumCollection = BuildCollection(featureCount: 1_000);
        _largeCollection = BuildCollection(featureCount: 10_000);
    }

    [Benchmark(Baseline = true, Description = "Serialize small (10 features)")]
    public byte[] SerializeSmall() => SerializeToBytes(_smallCollection);

    [Benchmark(Description = "Serialize medium (1k features)")]
    public byte[] SerializeMedium() => SerializeToBytes(_mediumCollection);

    [Benchmark(Description = "Serialize large (10k features)")]
    public byte[] SerializeLarge() => SerializeToBytes(_largeCollection);

    private static byte[] SerializeToBytes(FeatureCollection collection)
    {
        // Write to a pooled MemoryStream rather than allocating a string so the
        // benchmark mirrors the production endpoint, which streams via
        // Results.Json into the response pipe.
        using var stream = new MemoryStream(capacity: 64 * 1024);
        JsonSerializer.Serialize(stream, collection, OgcJsonContext.Default.FeatureCollection);
        return stream.ToArray();
    }

    private static FeatureCollection BuildCollection(int featureCount)
    {
        var features = new GeoJsonFeature[featureCount];
        for (var i = 0; i < featureCount; i++)
        {
            var x = -180.0 + ((i * 0.001) % 360.0);
            var y = -90.0 + ((i * 0.001) % 180.0);
            var properties = new Dictionary<string, object?>(capacity: 5)
            {
                ["name"] = "feature-" + i.ToString(CultureInfo.InvariantCulture),
                ["category"] = (i % 4) switch
                {
                    0 => "road",
                    1 => "rail",
                    2 => "river",
                    _ => "trail",
                },
                ["length_m"] = 12.5 + i,
                ["active"] = (i % 2) == 0,
                ["observed_at"] = "2026-05-20T12:00:00Z",
            };

            features[i] = new GeoJsonFeature
            {
                Id = i,
                Geometry = new SimpleGeoJsonGeometry
                {
                    Type = "Point",
                    CoordinatesJson = FormattableString.Invariant($"[{x:F4},{y:F4}]"),
                },
                Properties = properties,
            };
        }

        return new FeatureCollection
        {
            Features = features,
            NumberMatched = featureCount,
            NumberReturned = featureCount,
        };
    }
}
