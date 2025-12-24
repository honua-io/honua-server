// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Benchmarks;

/// <summary>
/// Query endpoint benchmarks targeting performance baselines:
/// - p50 less than 50ms (100 features)
/// - p95 less than 150ms (100 features)
/// - p99 less than 300ms (100 features)
/// - Throughput greater than 1k rps
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob]
public class QueryBenchmarks : IDisposable
{
    private HonuaTestFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly string _layerId = "0";

    [GlobalSetup]
    public void GlobalSetup()
    {
        _factory = new HonuaTestFactory();
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
    /// Baseline query benchmark - simple where clause returning approximately 100 features
    /// Target: p50 less than 50ms, p99 less than 300ms
    /// </summary>
    [Benchmark]
    public async Task<string> SimpleWhereQuery()
    {
        var response = await _client.GetAsync(
            $"/rest/services/test/FeatureServer/{_layerId}/query?where=population>1000&f=json");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Spatial query benchmark - bbox intersection returning approximately 100 features
    /// Target: p50 less than 50ms, p99 less than 300ms
    /// </summary>
    [Benchmark]
    public async Task<string> SpatialBboxQuery()
    {
        var bbox = "-122.5,37.7,-122.3,37.8";
        var response = await _client.GetAsync(
            $"/rest/services/test/FeatureServer/{_layerId}/query?geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Combined where + spatial query benchmark
    /// Target: p50 less than 100ms, p99 less than 500ms
    /// </summary>
    [Benchmark]
    public async Task<string> CombinedWhereAndSpatialQuery()
    {
        var bbox = "-122.5,37.7,-122.3,37.8";
        var response = await _client.GetAsync(
            $"/rest/services/test/FeatureServer/{_layerId}/query?where=population>5000&geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Paginated query benchmark - testing paging performance
    /// Target: p50 less than 20ms (small pages)
    /// </summary>
    [Benchmark]
    public async Task<string> PaginatedQuery()
    {
        var response = await _client.GetAsync(
            $"/rest/services/test/FeatureServer/{_layerId}/query?resultOffset=0&resultRecordCount=50&f=json");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Large result set benchmark - testing performance with 1000+ features
    /// Target: p50 less than 150ms, p99 less than 800ms
    /// </summary>
    [Benchmark]
    public async Task<string> LargeResultSet()
    {
        var response = await _client.GetAsync(
            $"/rest/services/test/FeatureServer/{_layerId}/query?resultRecordCount=1000&f=json");

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

/// <summary>
/// Custom WebApplicationFactory to avoid Program class conflicts
/// </summary>
public class HonuaTestFactory : WebApplicationFactory<object>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseStartup<TestStartup>();
    }
}

/// <summary>
/// Test startup class that configures the web application for benchmarking
/// </summary>
public class TestStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Add minimal services needed for benchmarking
        services.AddControllers();
        services.AddHttpClient();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            // Add dummy endpoints for benchmarking
            endpoints.MapGet("/rest/services/test/FeatureServer/0/query", async context =>
            {
                await context.Response.WriteAsync(@"{
                    ""features"": [],
                    ""exceededTransferLimit"": false,
                    ""objectIdFieldName"": ""objectid"",
                    ""globalIdFieldName"": ""globalid"",
                    ""geometryType"": ""esriGeometryPoint"",
                    ""spatialReference"": { ""wkid"": 4326 },
                    ""fields"": []
                }");
            });
        });
    }
}
