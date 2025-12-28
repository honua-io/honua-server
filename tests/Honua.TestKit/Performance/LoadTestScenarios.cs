// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    private static readonly string[] _cqlFilters =
    {
        "name LIKE '%test%'",
        "status = 'active' AND created_at > '2023-01-01'",
        "ST_INTERSECTS(geometry, POLYGON((0 0, 1 0, 1 1, 0 1, 0 0)))",
        "(category IN ('A', 'B', 'C')) AND (value > 100)"
    };

    private static readonly string[] _connectionPoolEndpoints =
    {
        "/rest/services/test/FeatureServer",
        "/rest/services/test/FeatureServer/0/query?f=json&where=1=1",
        "/ogc/features/collections",
        "/healthz/ready"
    };

    /// <summary>
    /// Basic query performance test for FeatureServer endpoints.
    /// Tests response times under normal load conditions.
    /// </summary>
    public static ScenarioProps CreateFeatureQueryScenario(string baseUrl, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create("feature_query_load", async _ =>
            {
                // Test simple feature query
                var response = await httpClient.GetAsync($"{baseUrl}/rest/services/test/FeatureServer/{layerId}/query?f=json&where=1=1");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(
                // Ramp up to 50 concurrent users over 1 minute
                Simulation.RampingConstant(copies: 50, during: TimeSpan.FromMinutes(1)),
                // Maintain 50 concurrent users for 2 minutes
                Simulation.KeepConstant(copies: 50, during: TimeSpan.FromMinutes(2)),
                // Ramp down over 30 seconds
                Simulation.RampingConstant(copies: 0, during: TimeSpan.FromSeconds(30))
            )
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
    public static ScenarioProps CreateSpatialQueryScenario(string baseUrl, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create("spatial_query_load", async context =>
            {
                // Generate random bounding boxes for testing
                var random = new Random(context.InvocationNumber.GetHashCode());
                var minX = random.NextDouble() * 360 - 180;
                var minY = random.NextDouble() * 180 - 90;
                var maxX = minX + random.NextDouble() * 10;
                var maxY = minY + random.NextDouble() * 10;

                var bbox = $"{minX},{minY},{maxX},{maxY}";
                var response = await httpClient.GetAsync(
                    $"{baseUrl}/rest/services/test/FeatureServer/{layerId}/query?f=json&geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(
                Simulation.Inject(rate: 5, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(2))
            )
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
    public static ScenarioProps CreateOgcQueryScenario(string baseUrl, string collectionId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create("ogc_query_load", async _ =>
            {
                // Test OGC API Features endpoint
                var response = await httpClient.GetAsync($"{baseUrl}/ogc/features/collections/{collectionId}/items?limit=10");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(
                Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(1))
            )
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
    public static ScenarioProps CreateCqlFilterScenario(string baseUrl, string collectionId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create("cql_filter_load", async context =>
            {
                var filterIndex = (int)(context.InvocationNumber % _cqlFilters.Length);
                var filter = _cqlFilters[filterIndex];
                var response = await httpClient.GetAsync($"{baseUrl}/ogc/features/collections/{collectionId}/items?filter={Uri.EscapeDataString(filter)}");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(
                Simulation.Inject(rate: 5, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(2))
            )
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
    public static ScenarioProps CreateConnectionPoolScenario(string baseUrl)
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create("connection_pool_stress", async context =>
            {
                // Mix of different endpoints to stress connection pool
                var endpointIndex = (int)(context.InvocationNumber % _connectionPoolEndpoints.Length);
                var endpoint = _connectionPoolEndpoints[endpointIndex];
                var response = await httpClient.GetAsync($"{baseUrl}{endpoint}");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(
                // Burst load to test connection pool limits
                Simulation.RampingConstant(copies: 200, during: TimeSpan.FromSeconds(30)),
                Simulation.KeepConstant(copies: 200, during: TimeSpan.FromMinutes(1))
            )
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
    public static ScenarioProps CreateMemoryStressScenario(string baseUrl, string layerId = "0")
    {
        var httpClient = Http.CreateDefaultClient();

        return Scenario.Create("memory_stress", async _ =>
            {
                // Request large result sets
                var response = await httpClient.GetAsync(
                    $"{baseUrl}/rest/services/test/FeatureServer/{layerId}/query?f=json&where=1=1&resultRecordCount=1000");

                return response.IsSuccessStatusCode ? Response.Ok() : Response.Fail();
            })
            .WithLoadSimulations(
                Simulation.Inject(rate: 2, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(3))
            )
            .WithClean(_ =>
            {
                httpClient.Dispose();
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Creates a comprehensive load test suite combining multiple scenarios.
    /// </summary>
    public static NBomberContext CreateLoadTestSuite(string baseUrl)
    {
        return NBomberRunner
            .RegisterScenarios(
                CreateFeatureQueryScenario(baseUrl),
                CreateSpatialQueryScenario(baseUrl),
                CreateOgcQueryScenario(baseUrl),
                CreateCqlFilterScenario(baseUrl),
                CreateConnectionPoolScenario(baseUrl),
                CreateMemoryStressScenario(baseUrl)
            )
            .WithReportFolder("performance-reports")
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv);
    }
}
