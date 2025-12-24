// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Benchmarks;

/// <summary>
/// Query endpoint benchmarks targeting performance baselines (Issue #46):
/// - p50 less than 30ms (basic queries)
/// - p95 less than 100ms (basic queries) - AC requirement from Issue #46
/// - p99 less than 200ms (basic queries)
/// - Throughput greater than 1k rps
///
/// These benchmarks use the real Honua Server application via WebApplicationFactory
/// to measure actual endpoint performance including serialization, database access,
/// and the full request pipeline.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob]
public class QueryBenchmarks : IDisposable
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private const string LayerId = "0";

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Create factory using real Honua Server application
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Enable dev auth to bypass authentication in benchmarks
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseEnvironment("Testing");
            });

        _client = _factory.CreateClient();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Baseline query benchmark - simple where clause returning approximately 100 features.
    /// Target: p50 less than 30ms, p95 less than 100ms, p99 less than 200ms
    /// </summary>
    [Benchmark(Description = "Simple WHERE clause query")]
    public async Task<string> SimpleWhereQuery()
    {
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?where=1=1&resultRecordCount=100&f=json");

        // Read response to ensure full request completion
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Spatial query benchmark - bbox intersection returning approximately 100 features.
    /// Target: p50 less than 30ms, p95 less than 100ms, p99 less than 200ms
    /// </summary>
    [Benchmark(Description = "Spatial bbox query")]
    public async Task<string> SpatialBboxQuery()
    {
        var bbox = "-122.5,37.7,-122.3,37.8";
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Combined where + spatial query benchmark
    /// Target: p50 less than 100ms, p99 less than 500ms
    /// </summary>
    [Benchmark(Description = "Combined WHERE + spatial query")]
    public async Task<string> CombinedWhereAndSpatialQuery()
    {
        var bbox = "-122.5,37.7,-122.3,37.8";
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?where=1=1&geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Paginated query benchmark - testing paging performance
    /// Target: p50 less than 20ms (small pages)
    /// </summary>
    [Benchmark(Description = "Paginated query (offset/limit)")]
    public async Task<string> PaginatedQuery()
    {
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?resultOffset=0&resultRecordCount=50&f=json");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Large result set benchmark - testing performance with 1000+ features
    /// Target: p50 less than 150ms, p99 less than 800ms
    /// </summary>
    [Benchmark(Description = "Large result set (1000 features)")]
    public async Task<string> LargeResultSet()
    {
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?resultRecordCount=1000&f=json");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// GeoJSON format output benchmark - tests JSON serialization performance
    /// </summary>
    [Benchmark(Description = "GeoJSON format query")]
    public async Task<string> GeoJsonFormatQuery()
    {
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?where=1=1&resultRecordCount=100&f=geojson");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Count-only query benchmark - tests window function optimization
    /// </summary>
    [Benchmark(Description = "Count-only query")]
    public async Task<string> CountOnlyQuery()
    {
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?where=1=1&returnCountOnly=true&f=json");

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// OutFields restriction benchmark - tests field filtering
    /// </summary>
    [Benchmark(Description = "Query with outFields restriction")]
    public async Task<string> OutFieldsQuery()
    {
        var response = await _client!.GetAsync(
            $"/rest/services/test/FeatureServer/{LayerId}/query?where=1=1&outFields=objectid,name&resultRecordCount=100&f=json");

        return await response.Content.ReadAsStringAsync();
    }
}
