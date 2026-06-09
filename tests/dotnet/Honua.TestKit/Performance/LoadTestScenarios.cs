// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Honua.TestKit.Performance;

/// <summary>
/// Load testing scenarios for geospatial API endpoints.
/// Ensures performance requirements are met under various load conditions.
/// </summary>
public static class LoadTestScenarios
{
    public const string FeatureQueryScenarioName = "feature_query_load";
    public const string SpatialQueryScenarioName = "spatial_query_load";
    public const string OgcQueryScenarioName = "ogc_query_load";
    public const string CqlFilterScenarioName = "cql_filter_load";
    public const string ODataQueryScenarioName = "odata_query_load";
    public const string ConnectionPoolScenarioName = "connection_pool_stress";
    public const string MemoryStressScenarioName = "memory_stress";
    public const string TilesScenarioName = "tiles_load";

    private static readonly string[] _scenarioNames =
    [
        FeatureQueryScenarioName,
        SpatialQueryScenarioName,
        OgcQueryScenarioName,
        CqlFilterScenarioName,
        ODataQueryScenarioName,
        ConnectionPoolScenarioName,
        MemoryStressScenarioName,
        TilesScenarioName
    ];

    /// <summary>
    /// Gets the scenario names registered by <see cref="CreateLoadTestSuite"/>.
    /// </summary>
    public static IReadOnlyList<string> ScenarioNames => _scenarioNames;

    private static readonly string[] _cqlFilters =
    {
        "name LIKE '%test%'",
        "category = 'test' AND timestamp > TIMESTAMP('2023-01-01T00:00:00Z')",
        "S_INTERSECTS(shape, POLYGON((-123 37, -123 38, -122 38, -122 37, -123 37)))",
        "(category IN ('test', 'sample')) AND (objectid > 1)"
    };

    private static readonly string[] _connectionPoolEndpoints =
    {
        "/rest/services/test/FeatureServer",
        "/rest/services/test/FeatureServer/0/query?f=json&where=1=1",
        "/ogc/features/collections",
        "/healthz/ready"
    };

    private static readonly string[] _odataQueryTemplates =
    {
        "/odata/Features({0})?$top=10",
        "/odata/Features({0})?$top=5&$orderby=ObjectId desc",
        "/odata/Features({0})?$filter=ObjectId gt 1&$top=5",
        "/odata/Features({0})?$select=ObjectId,LayerId&$top=10",
        "/odata/Layers?$top=5"
    };

    private static LoadSimulation[] CreateLoadSimulations(int copies, LoadTestProfile profile)
    {
        return new[]
        {
            Simulation.RampingConstant(copies, profile.RampUp),
            Simulation.KeepConstant(copies, profile.Duration),
            Simulation.RampingConstant(0, profile.RampDown)
        };
    }

    /// <summary>
    /// Basic query performance test for FeatureServer endpoints.
    /// Tests response times under normal load conditions.
    /// </summary>
    public static ScenarioProps CreateFeatureQueryScenario(string baseUrl, LoadTestProfile profile, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(FeatureQueryScenarioName, async _ =>
            {
                // Test simple feature query
                var response = await httpClient.GetAsync($"{baseUrl}/rest/services/test/FeatureServer/{layerId}/query?f=json&where=1=1");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.FeatureQueryUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Spatial query performance test with bounding box filters.
    /// Tests performance of spatial indexing and filtering.
    /// </summary>
    public static ScenarioProps CreateSpatialQueryScenario(string baseUrl, LoadTestProfile profile, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(SpatialQueryScenarioName, async context =>
            {
                // Keep generated envelopes inside the seeded test extent and below configured spatial limits.
                var random = new Random(context.InvocationNumber.GetHashCode());
                var centerX = -122.50 + random.NextDouble() * 0.20;
                var centerY = 37.70 + random.NextDouble() * 0.12;
                var halfWidth = 0.0025 + random.NextDouble() * 0.005;
                var halfHeight = 0.0025 + random.NextDouble() * 0.005;
                var minX = centerX - halfWidth;
                var minY = centerY - halfHeight;
                var maxX = centerX + halfWidth;
                var maxY = centerY + halfHeight;

                var bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", minX, minY, maxX, maxY);
                var response = await httpClient.GetAsync(
                    $"{baseUrl}/rest/services/test/FeatureServer/{layerId}/query?f=json&geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&inSR=4326");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.SpatialQueryUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// OGC API Features performance test.
    /// Tests OGC endpoint performance compared to FeatureServer.
    /// </summary>
    public static ScenarioProps CreateOgcQueryScenario(string baseUrl, LoadTestProfile profile, string collectionId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(OgcQueryScenarioName, async _ =>
            {
                // Test OGC API Features endpoint
                var response = await httpClient.GetAsync($"{baseUrl}/ogc/features/collections/{collectionId}/items?limit=10");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.OgcFeaturesUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Complex CQL2 filter performance test.
    /// Tests performance of filter parsing and SQL translation.
    /// </summary>
    public static ScenarioProps CreateCqlFilterScenario(string baseUrl, LoadTestProfile profile, string collectionId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(CqlFilterScenarioName, async context =>
            {
                var filterIndex = (int)(context.InvocationNumber % _cqlFilters.Length);
                var filter = _cqlFilters[filterIndex];
                var response = await httpClient.GetAsync($"{baseUrl}/ogc/features/collections/{collectionId}/items?filter={Uri.EscapeDataString(filter)}");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.CqlUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// OData query performance test.
    /// Tests OData query processing under sustained load.
    /// </summary>
    public static ScenarioProps CreateODataQueryScenario(string baseUrl, LoadTestProfile profile, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(ODataQueryScenarioName, async context =>
            {
                var templateIndex = (int)(context.InvocationNumber % _odataQueryTemplates.Length);
                var template = _odataQueryTemplates[templateIndex];
                var endpoint = string.Format(CultureInfo.InvariantCulture, template, layerId);
                var response = await httpClient.GetAsync($"{baseUrl}{endpoint}");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.ODataUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Database connection pool stress test.
    /// Tests system behavior under high connection load.
    /// </summary>
    public static ScenarioProps CreateConnectionPoolScenario(string baseUrl, LoadTestProfile profile)
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(ConnectionPoolScenarioName, async context =>
            {
                // Mix of different endpoints to stress connection pool
                var endpointIndex = (int)(context.InvocationNumber % _connectionPoolEndpoints.Length);
                var endpoint = _connectionPoolEndpoints[endpointIndex];
                var response = await httpClient.GetAsync($"{baseUrl}{endpoint}");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.ConnectionPoolUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Memory stress test with large result sets.
    /// Tests memory management under high data volume.
    /// </summary>
    public static ScenarioProps CreateMemoryStressScenario(string baseUrl, LoadTestProfile profile, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(MemoryStressScenarioName, async _ =>
            {
                // Request large result sets
                var response = await httpClient.GetAsync(
                    $"{baseUrl}/rest/services/test/FeatureServer/{layerId}/query?f=json&where=1=1&resultRecordCount=1000");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.MemoryStressUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// OGC API Tiles performance test.
    /// Tests tile rendering and MVT encoding under sustained load.
    /// </summary>
    public static ScenarioProps CreateTilesScenario(
        string baseUrl,
        LoadTestProfile profile,
        string collectionId = "0",
        string tileMatrixSetId = "WebMercatorQuad")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create(TilesScenarioName, async context =>
            {
                var random = new Random(context.InvocationNumber.GetHashCode());
                var zoom = random.Next(0, 3);
                var maxIndex = 1 << zoom;
                var row = random.Next(0, maxIndex);
                var col = random.Next(0, maxIndex);

                var response = await httpClient.GetAsync(
                    $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{zoom}/{row}/{col}");

                return response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent
                    ? Response.Ok()
                    : Response.Fail();
            })
            .WithLoadSimulations(CreateLoadSimulations(profile.TilesUsers, profile))
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Creates a comprehensive load test suite combining multiple scenarios.
    /// </summary>
    public static NBomberContext CreateLoadTestSuite(
        string baseUrl,
        LoadTestProfile profile,
        string layerId = "0",
        string collectionId = "0",
        string tileMatrixSetId = "WebMercatorQuad",
        string? reportFolder = null,
        ReportFormat[]? reportFormats = null)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        reportFolder ??= "load-test-reports";
        reportFormats ??= new[] { ReportFormat.Html, ReportFormat.Csv };

        var scenarios = new List<ScenarioProps>();

        if (profile.FeatureQueryUsers > 0)
        {
            scenarios.Add(CreateFeatureQueryScenario(baseUrl, profile, layerId));
        }

        if (profile.SpatialQueryUsers > 0)
        {
            scenarios.Add(CreateSpatialQueryScenario(baseUrl, profile, layerId));
        }

        if (profile.OgcFeaturesUsers > 0)
        {
            scenarios.Add(CreateOgcQueryScenario(baseUrl, profile, collectionId));
        }

        if (profile.CqlUsers > 0)
        {
            scenarios.Add(CreateCqlFilterScenario(baseUrl, profile, collectionId));
        }

        if (profile.ODataUsers > 0)
        {
            scenarios.Add(CreateODataQueryScenario(baseUrl, profile, layerId));
        }

        if (profile.ConnectionPoolUsers > 0)
        {
            scenarios.Add(CreateConnectionPoolScenario(baseUrl, profile));
        }

        if (profile.MemoryStressUsers > 0)
        {
            scenarios.Add(CreateMemoryStressScenario(baseUrl, profile, layerId));
        }

        if (profile.TilesUsers > 0)
        {
            scenarios.Add(CreateTilesScenario(baseUrl, profile, collectionId, tileMatrixSetId));
        }

        return NBomberRunner
            .RegisterScenarios(scenarios.ToArray())
            .WithReportFolder(reportFolder)
            .WithReportFormats(reportFormats);
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl.TrimEnd('/');
    }
}
