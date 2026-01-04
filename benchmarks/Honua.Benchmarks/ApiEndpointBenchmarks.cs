// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Benchmarks;

/// <summary>
/// Comprehensive API endpoint performance benchmarks covering all supported protocols:
/// - FeatureServer (Esri GeoServices REST API)
/// - OData v4 (Microsoft OData protocol)
/// - OGC API Features (Open Geospatial Consortium standard)
/// - MVT Tiles (Mapbox Vector Tiles)
///
/// Performance targets for enterprise geospatial API workloads:
/// - Query responses: &lt;100ms p95 for simple queries, &lt;500ms p95 for complex
/// - Metadata endpoints: &lt;50ms p95
/// - Tile generation: &lt;200ms p95 for zoom levels 0-10
/// - Throughput: &gt;1000 requests/second for simple queries
/// - Memory usage: &lt;50MB for concurrent requests
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class ApiEndpointBenchmarks : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = null!;
    private readonly HttpClient _client = null!;

    // Test data identifiers
    private const string TestServiceId = "1";
    private const string TestLayerId = "0";
    private const string TestCollectionId = "0";

    // Pre-built query parameters for different test scenarios
    private readonly Dictionary<string, string> _featureServerQueries = new()
    {
        ["simple"] = "f=json&where=1=1&resultRecordCount=10",
        ["filtered"] = "f=json&where=category='urban'&resultRecordCount=100",
        ["spatial"] = "f=json&geometry=-158,-21,-157,22&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&resultRecordCount=50",
        ["complex"] = "f=json&where=category IN ('urban','industrial')&geometry=-158,-21,-157,22&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&orderByFields=objectid&resultRecordCount=200",
        ["paginated"] = "f=json&where=1=1&orderByFields=objectid&resultOffset=1000&resultRecordCount=100",
        ["geojson"] = "f=geojson&where=1=1&resultRecordCount=50",
        ["count"] = "f=json&where=1=1&returnCountOnly=true",
        ["ids"] = "f=json&objectIds=1,2,3,4,5,6,7,8,9,10"
    };

    private readonly Dictionary<string, string> _ogcQueries = new()
    {
        ["simple"] = "limit=10",
        ["filtered"] = "limit=100&cql-filter=category='urban'",
        ["spatial"] = "limit=50&bbox=-158,21,-157,22",
        ["complex"] = "limit=200&cql-filter=category IN ('urban','industrial') AND ST_INTERSECTS(geometry,POLYGON((-158 21,-157 21,-157 22,-158 22,-158 21)))",
        ["paginated"] = "limit=100&offset=1000"
    };

    private readonly Dictionary<string, string> _odataQueries = new()
    {
        ["simple"] = "$top=10",
        ["filtered"] = "$filter=category eq 'urban'&$top=100",
        ["spatial"] = "$filter=st_intersects(geometry,geography'POLYGON((-158 21,-157 21,-157 22,-158 22,-158 21))')&$top=50",
        ["complex"] = "$filter=(category eq 'urban' or category eq 'industrial') and st_intersects(geometry,geography'POLYGON((-158 21,-157 21,-157 22,-158 22,-158 21))')&$orderby=ObjectId&$top=200",
        ["paginated"] = "$skip=1000&$top=100&$orderby=ObjectId",
        ["count"] = "$count=true&$top=0",
        ["select"] = "$select=ObjectId,LayerId,category&$top=10"
    };

    [Params("simple", "filtered", "spatial", "complex", "paginated")]
    public string QueryType { get; set; } = "simple";

    #region FeatureServer API Benchmarks

    [Benchmark(Description = "FeatureServer - Service Info")]
    public async Task<HttpResponseMessage> FeatureServerServiceInfo()
    {
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=json");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "FeatureServer - Layer Info")]
    public async Task<HttpResponseMessage>
    FeatureServerLayerInfo()
    {
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=json");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "FeatureServer - Query Features")]
    public async Task<HttpResponseMessage>
    FeatureServerQuery()
    {
        var query = _featureServerQueries[QueryType];
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?{query}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "FeatureServer - Query Features (GeoJSON)")]
    public async Task<HttpResponseMessage>
    FeatureServerQueryGeoJson()
    {
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?{_featureServerQueries["geojson"]}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "FeatureServer - Count Only")]
    public async Task<HttpResponseMessage>
    FeatureServerCountQuery()
    {
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?{_featureServerQueries["count"]}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "FeatureServer - Object IDs Query")]
    public async Task<HttpResponseMessage>
    FeatureServerObjectIdsQuery()
    {
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?{_featureServerQueries["ids"]}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "FeatureServer - ApplyEdits (Bulk Create)")]
    public async Task<HttpResponseMessage>
    FeatureServerApplyEditsCreate()
    {
        var features = GenerateFeatureServerFeatures(10);
        var edits = new
        {
            adds = features,
            updates = Array.Empty<object>(),
            deletes = Array.Empty<long>()
        }
    ;

        var content = JsonContent.Create(edits);
        var response = await _client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);
        response.EnsureSuccessStatusCode();
        return response;
    }

    #endregion

    #region OGC API Features Benchmarks

    [Benchmark(Description = "OGC API - Landing Page")]
    public async Task<HttpResponseMessage>
OgcApiLandingPage()
    {
        var response = await _client.GetAsync("/ogc/features/");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OGC API - Conformance")]
    public async Task<HttpResponseMessage>
    OgcApiConformance()
    {
        var response = await _client.GetAsync("/ogc/features/conformance");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OGC API - Collections")]
    public async Task<HttpResponseMessage>
    OgcApiCollections()
    {
        var response = await _client.GetAsync("/ogc/features/collections");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OGC API - Collection Info")]
    public async Task<HttpResponseMessage>
    OgcApiCollectionInfo()
    {
        var response = await _client.GetAsync($"/ogc/features/collections/{TestCollectionId}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OGC API - Items Query")]
    public async Task<HttpResponseMessage>
    OgcApiItemsQuery()
    {
        var query = _ogcQueries[QueryType];
        var response = await _client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?{query}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OGC API - Single Item")]
    public async Task<HttpResponseMessage>
    OgcApiSingleItem()
    {
        var response = await _client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items/1");
        response.EnsureSuccessStatusCode();
        return response;
    }

    #endregion

    #region OData v4 API Benchmarks

    [Benchmark(Description = "OData - Service Document")]
    public async Task<HttpResponseMessage>
    ODataServiceDocument()
    {
        var response = await _client.GetAsync("/odata/");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OData - Metadata")]
    public async Task<HttpResponseMessage>
    ODataMetadata()
    {
        var response = await _client.GetAsync("/odata/$metadata");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OData - Layers")]
    public async Task<HttpResponseMessage>
    ODataLayers()
    {
        var response = await _client.GetAsync("/odata/Layers?$top=10");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OData - Features Query")]
    public async Task<HttpResponseMessage>
    ODataFeaturesQuery()
    {
        var query = _odataQueries[QueryType];
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?{query}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OData - Features Count")]
    public async Task<HttpResponseMessage>
    ODataFeaturesCount()
    {
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?{_odataQueries["count"]}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "OData - Features Select")]
    public async Task<HttpResponseMessage>
    ODataFeaturesSelect()
    {
        var response = await _client.GetAsync($"/odata/Features({TestLayerId})?{_odataQueries["select"]}");
        response.EnsureSuccessStatusCode();
        return response;
    }

    #endregion

    #region MVT Tiles Benchmarks

    [Benchmark(Description = "MVT Tiles - Tile Metadata")]
    public async Task<HttpResponseMessage>
    MvtTileMetadata()
    {
        var response = await _client.GetAsync($"/ogc/tiles/collections/{TestCollectionId}/tiles/WebMercatorQuad");
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "MVT Tiles - Low Zoom Tile")]
    public async Task<HttpResponseMessage>
    MvtLowZoomTile()
    {
        var response = await _client.GetAsync($"/ogc/tiles/collections/{TestCollectionId}/tiles/WebMercatorQuad/2/1/1");
        // 204 No Content is acceptable for empty tiles
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NoContent)
        {
            response.EnsureSuccessStatusCode();
        }
        return response;
    }

    [Benchmark(Description = "MVT Tiles - Medium Zoom Tile")]
    public async Task<HttpResponseMessage>
    MvtMediumZoomTile()
    {
        var response = await _client.GetAsync($"/ogc/tiles/collections/{TestCollectionId}/tiles/WebMercatorQuad/6/32/32");
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NoContent)
        {
            response.EnsureSuccessStatusCode();
        }
        return response;
    }

    [Benchmark(Description = "MVT Tiles - High Zoom Tile")]
    public async Task<HttpResponseMessage>
    MvtHighZoomTile()
    {
        var response = await _client.GetAsync($"/ogc/tiles/collections/{TestCollectionId}/tiles/WebMercatorQuad/10/512/512");
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NoContent)
        {
            response.EnsureSuccessStatusCode();
        }
        return response;
    }

    #endregion

    #region Cross-Protocol Performance Comparison

    [Benchmark(Description = "Cross-Protocol - Simple Query Comparison")]
    public async Task<(TimeSpan FeatureServer, TimeSpan OgcApi, TimeSpan OData)> CrossProtocolSimpleQuery()
    {
        var start1 = DateTime.UtcNow;
        await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&where=1=1&resultRecordCount=10");
        var featureServerTime = DateTime.UtcNow - start1;

        var start2 = DateTime.UtcNow;
        await _client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?limit=10");
        var ogcTime = DateTime.UtcNow - start2;

        var start3 = DateTime.UtcNow;
        await _client.GetAsync($"/odata/Features({TestLayerId})?$top=10");
        var odataTime = DateTime.UtcNow - start3;

        return (featureServerTime, ogcTime, odataTime);
    }

    [Benchmark(Description = "Cross-Protocol - Metadata Performance")]
    public async Task<(TimeSpan FeatureServer, TimeSpan OgcApi, TimeSpan OData)> CrossProtocolMetadata()
    {
        var start1 = DateTime.UtcNow;
        await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=json");
        var featureServerTime = DateTime.UtcNow - start1;

        var start2 = DateTime.UtcNow;
        await _client.GetAsync("/ogc/features/collections");
        var ogcTime = DateTime.UtcNow - start2;

        var start3 = DateTime.UtcNow;
        await _client.GetAsync("/odata/$metadata");
        var odataTime = DateTime.UtcNow - start3;

        return (featureServerTime, ogcTime, odataTime);
    }

    #endregion

    #region Content Negotiation and Format Benchmarks

    [Benchmark(Description = "Content Negotiation - JSON")]
    public async Task<HttpResponseMessage>
    ContentNegotiationJson()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/features/collections/{TestCollectionId}/items?limit=10");
        request.Headers.Accept.ParseAdd("application/json");
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "Content Negotiation - GeoJSON")]
    public async Task<HttpResponseMessage>
    ContentNegotiationGeoJson()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ogc/features/collections/{TestCollectionId}/items?limit=10");
        request.Headers.Accept.ParseAdd("application/geo+json");
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Benchmark(Description = "Content Negotiation - HTML")]
    public async Task<HttpResponseMessage>
    ContentNegotiationHtml()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ogc/features/");
        request.Headers.Accept.ParseAdd("text/html");
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    #endregion

    #region Error Handling Performance

    [Benchmark(Description = "Error Handling - 400 Bad Request")]
    public async Task<HttpResponseMessage>
    ErrorHandling400()
    {
        var response = await _client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&where=invalid_syntax_here");
        // Expecting 400, don't ensure success
        return response;
    }

    [Benchmark(Description = "Error Handling - 404 Not Found")]
    public async Task<HttpResponseMessage>
    ErrorHandling404()
    {
        var response = await _client.GetAsync("/rest/services/999999/FeatureServer/0");
        // Expecting 404, don't ensure success
        return response;
    }

    #endregion

    private static object[] GenerateFeatureServerFeatures(int count)
    {
        var features = new object[count];
        var random = new Random();

        for (int i = 0; i < count; i++)
        {
            features[i] = new
            {
                geometry = new
                {
                    x = -158.0 + (random.NextDouble() * 4),
                    y = 19.0 + (random.NextDouble() * 4),
                    spatialReference = new { wkid = 4326 }
                },
                attributes = new Dictionary<string, object?>
                {
                    ["name"] = $"BenchFeature_{i}",
                    ["category"] = random.Next(0, 2) == 0 ? "urban" : "rural",
                    ["priority"] = random.Next(1, 11),
                    ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            }
        ;
        }

        return features;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }
}
