// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using BenchmarkDotNet.Attributes;
using Honua.Core.Features.Caching;

namespace Honua.Benchmarks;

/// <summary>
/// Metadata cache key construction runs on every Redis read/write for the
/// metadata layer. It SHA-256 hashes the source URL and auth scope, then
/// canonicalises a dozen-odd components into a Redis-safe namespace string.
/// All of that is on the hot path under load, so we benchmark each public
/// entry point (full Build, key-only BuildKey, source fingerprint, auth-scope
/// fingerprint) to surface any regression in hashing or normalization.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Cache)]
public class CacheKeyHashBenchmarks
{
    private MetadataCacheKeyRequest _request = null!;
    private string _sourceUrl = null!;
    private string _authScope = null!;

    [GlobalSetup]
    public void Setup()
    {
        // A realistic adapter request: STAC endpoint with query string,
        // a long-ish tenant/project, a resource id with mixed casing, and
        // adapter/projection version tokens that participate in the key.
        _sourceUrl = "https://example.gov/stac/v1/collections/sentinel-2-l2a?token=secret&bbox=-122.5,37.7,-122.3,37.9";
        _authScope = "role:viewer|scopes:read:metadata,read:collections|tenant:acme-aerospace";

        _request = new MetadataCacheKeyRequest
        {
            KeyPrefix = MetadataCacheKeyBuilder.DefaultKeyPrefix,
            TenantId = "acme-aerospace-prod-01",
            ProjectId = "wildfire-monitoring-2026-q2",
            AuthScope = _authScope,
            SourceUrl = _sourceUrl,
            Adapter = "stac",
            Protocol = "stac",
            ResourceKind = "collection",
            ResourceId = "Sentinel-2-L2A",
            Crs = "EPSG:4326",
            Format = "application/json",
            AdapterVersion = "1.0.0",
            ProjectionVersion = "PROJ-9.4.0",
        };
    }

    [Benchmark(Baseline = true, Description = "Build full MetadataCacheKey")]
    public MetadataCacheKey BuildFull()
        => MetadataCacheKeyBuilder.Build(_request);

    [Benchmark(Description = "BuildKey (string only)")]
    public string BuildKeyOnly()
        => MetadataCacheKeyBuilder.BuildKey(_request);

    [Benchmark(Description = "FingerprintSource URL")]
    public string FingerprintSource()
        => MetadataCacheKeyBuilder.FingerprintSource(_sourceUrl);

    [Benchmark(Description = "FingerprintAuthScope")]
    public string FingerprintAuthScope()
        => MetadataCacheKeyBuilder.FingerprintAuthScope(_authScope);

    [Benchmark(Description = "Build x10 (batch under load)")]
    public string BuildBatch()
    {
        // Approximates a request burst where the same caller reads ten
        // metadata resources back-to-back; key construction must amortise.
        string last = string.Empty;
        for (var i = 0; i < 10; i++)
        {
            var request = _request with
            {
                ResourceId = "Sentinel-2-L2A-" + i.ToString(CultureInfo.InvariantCulture),
            };
            last = MetadataCacheKeyBuilder.BuildKey(request);
        }

        return last;
    }
}
