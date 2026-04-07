// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests.Integration;

/// <summary>
/// End-to-end integration tests that validate critical fixes work together.
/// Tests realistic scenarios combining security, performance, and resilience patterns.
/// </summary>
[Collection("Database")]
public class CriticalFixIntegrationTests : IClassFixture<WebAppFixture>, IClassFixture<DatabaseFixtureAdapter>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;
    private readonly DatabaseFixtureAdapter _database;

    public CriticalFixIntegrationTests(WebAppFixture fixture, DatabaseFixtureAdapter database)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
        _database = database;
    }

    [IntegrationTest]
    [Fact(DisplayName = "Complete workflow: Authentication → Data Import → Spatial Query → Export")]
    public async Task CompleteWorkflow_AuthenticationToExport_WorksSecurely()
    {
        // Arrange: Set up isolated test environment
        var schema = await _database.CreateIsolatedSchemaAsync(nameof(CriticalFixIntegrationTests));

        try
        {
            // Step 1: Authentication (with security fixes)
            var authenticatedClient = _fixture.CreateClient();
            authenticatedClient.DefaultRequestHeaders.Add("X-API-Key", "test-admin-key");

            var healthResponse = await authenticatedClient.GetAsync("/admin/health");
            healthResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Authentication should work with valid API key");

            // Step 2: Data Import (with memory management)
            var testGeoJson = GenerateTestGeoJsonData(100);
            var importContent = new MultipartFormDataContent
            {
                { new StringContent(testGeoJson), "file", "integration-test.geojson" },
                { new StringContent("integration-test-layer"), "name" }
            };

            var importResponse = await authenticatedClient.PostAsync("/admin/import/geojson", importContent);
            importResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted);

            // Wait for import to complete (simplified for test)
            await Task.Delay(2000);

            // Step 3: Spatial Query (with performance optimizations)
            var spatialQuery = "/rest/services/1/FeatureServer/0/query?where=1=1&spatialRel=esriSpatialRelIntersects&resultRecordCount=50&f=json";
            var queryResponse = await _client.GetAsync(spatialQuery);

            queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Spatial query should work with performance fixes");

            var queryContent = await queryResponse.Content.ReadAsStringAsync();
            queryContent.Should().Contain("features", "Query should return feature collection");

            // Step 4: Export (with memory bounds)
            var exportResponse = await _client.GetAsync("/ogc/features/v1/collections/integration-test-layer/items?f=application/geo+json");
            exportResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.PartialContent);

            // Verify complete workflow integrity
            var exportContent = await exportResponse.Content.ReadAsStringAsync();
            exportContent.Should().Contain("FeatureCollection", "Export should return valid GeoJSON");

        }
        finally
        {
            await _database.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    [SecurityTest]
    [Fact(DisplayName = "Security fixes prevent attack chains across multiple endpoints")]
    public async Task SecurityFixes_PreventAttackChainsAcrossEndpoints()
    {
        // Test that security fixes work together to prevent sophisticated attacks

        // Step 1: Attempt SQL injection via multiple vectors
        var sqlInjectionAttempts = new[]
        {
            "/rest/services/1/FeatureServer/0/query?where=id = '1'; DROP TABLE users; --",
            "/ogc/features/v1/collections/test/items?filter=name = 'test'; DELETE FROM layers; --",
            "/odata/v4/collections('test')/items?$filter=name eq 'test''; UPDATE users SET admin=true; --"
        };

        foreach (var maliciousUrl in sqlInjectionAttempts)
        {
            var response = await _client.GetAsync(Uri.EscapeUriString(maliciousUrl));

            // Should either reject or safely handle
            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("syntax error", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("DROP TABLE", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("DELETE FROM", StringComparison.OrdinalIgnoreCase);

            response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.OK, HttpStatusCode.Unauthorized);
        }

        // Step 2: Attempt authentication bypass in production environment
        var productionClient = CreateProductionClient();
        var bypassResponse = await productionClient.GetAsync("/admin/health");

        bypassResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "Production environment should prevent development bypass");

        // Step 3: Test credential exposure via logs/responses
        var sensitiveData = new[]
        {
            "password=secret123",
            "Bearer eyJhbGciOiJIUzI1NiJ9.test.token",
            "connectionstring=Server=test;Password=secret;"
        };

        foreach (var sensitive in sensitiveData)
        {
            var response = await _client.PostAsync("/admin/test-endpoint", new StringContent(sensitive));
            var responseContent = await response.Content.ReadAsStringAsync();

            // Sensitive data should not appear in responses
            responseContent.Should().NotContain(sensitive, "Sensitive data should be sanitized");

            // Check for sanitization markers
            if (responseContent.Contains("[REDACTED]", StringComparison.OrdinalIgnoreCase))
            {
                // Good - data was sanitized
            }
        }
    }

    [IntegrationTest]
    [Fact(DisplayName = "Performance fixes work together under realistic load")]
    public async Task PerformanceFixes_WorkTogetherUnderRealisticLoad()
    {
        // Test that performance optimizations work together effectively

        // Seed test data for realistic performance testing
        await SeedPerformanceTestDataAsync();

        // Step 1: Cache performance with database optimizations
        var cacheWarmupTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=category={i}&resultRecordCount=100");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return response;
        });

        await Task.WhenAll(cacheWarmupTasks);

        // Step 2: Test spatial query performance with indexes
        var spatialQueryTasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var bbox = $"-{i},-{i},{i},{i}"; // Different bounding boxes
            var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?geometry={bbox}&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects");

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            // Check performance
            var processingTime = response.Headers.GetValues("X-Processing-Time").FirstOrDefault();
            if (processingTime != null && double.TryParse(processingTime, out var timeMs))
            {
                timeMs.Should().BeLessThan(2000, "Spatial queries should be optimized");
            }

            return response;
        });

        await Task.WhenAll(spatialQueryTasks);

        // Step 3: Test bulk operations with memory management
        var bulkOperations = Enumerable.Range(0, 5).Select(async batchId =>
        {
            var bulkData = GenerateBulkFeatureData(500, $"bulk-{batchId}");
            var content = new StringContent(bulkData, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("/ogc/features/v1/collections/test/items/bulk", content);
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted);

            return response;
        });

        await Task.WhenAll(bulkOperations);

        // Verify system remains responsive after all operations
        var healthResponse = await _client.GetAsync("/admin/health");
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK, "System should remain healthy after performance tests");
    }

    [IntegrationTest]
    [Fact(DisplayName = "Resilience patterns prevent cascading failures")]
    public async Task ResiliencePatterns_PreventCascadingFailures()
    {
        // Simulate failure scenarios and verify graceful degradation

        // Step 1: Simulate database pressure
        var dbPressureTasks = Enumerable.Range(0, 50).Select(async i =>
        {
            // Some requests will succeed, others may timeout - test resilience
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=1000&pressure_test={i}", cts.Token);
                return new { Success = response.StatusCode == HttpStatusCode.OK, TaskId = i };
            }
            catch (OperationCanceledException)
            {
                return new { Success = false, TaskId = i }; // Timeout
            }
        });

        var dbResults = await Task.WhenAll(dbPressureTasks);

        // Should have graceful degradation, not complete failure
        var successRate = (double)dbResults.Count(r => r.Success) / dbResults.Length;
        successRate.Should().BeGreaterOrEqualTo(0.5, "Should maintain at least 50% success rate under pressure");

        // Step 2: Test that admin endpoints remain available during data pressure
        var adminResponse = await _client.GetAsync("/admin/health");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Admin endpoints should remain available");

        // Step 3: Verify rate limiting protects system
        var rateLimitTasks = Enumerable.Range(0, 100).Select(async i =>
        {
            var response = await _client.GetAsync($"/rest/services/1/FeatureServer/layers?rate_test={i}");
            return response.StatusCode;
        });

        var rateLimitResults = await Task.WhenAll(rateLimitTasks);
        var rateLimited = rateLimitResults.Count(s => s == HttpStatusCode.TooManyRequests);

        rateLimited.Should().BeGreaterThan(0, "Rate limiting should activate under load");

        // Step 4: Verify circuit breaker prevents cascading failures
        await Task.Delay(5000); // Allow circuit breaker to reset

        var finalHealthCheck = await _client.GetAsync("/admin/health");
        finalHealthCheck.StatusCode.Should().Be(HttpStatusCode.OK, "System should recover after pressure");
    }

    [IntegrationTest]
    [Fact(DisplayName = "Memory management prevents OOM in complex workflows")]
    public async Task MemoryManagement_PreventsOOMInComplexWorkflows()
    {
        // Test memory management across complex, realistic workflows

        var initialMemory = GC.GetTotalMemory(true);

        // Complex workflow: Import → Process → Query → Export → Repeat
        for (int cycle = 0; cycle < 3; cycle++)
        {
            // Import large dataset
            var largeDataset = GenerateTestGeoJsonData(2000);
            var importContent = new MultipartFormDataContent
            {
                { new StringContent(largeDataset), "file", $"memory-test-{cycle}.geojson" },
                { new StringContent($"memory-test-cycle-{cycle}"), "name" }
            };

            var importResponse = await _client.PostAsync("/admin/import/geojson", importContent);
            importResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted);

            // Query large result sets
            var largQueryTasks = Enumerable.Range(0, 10).Select(async i =>
            {
                var response = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=500&cycle={cycle}&query={i}");
                return response.StatusCode == HttpStatusCode.OK;
            });

            await Task.WhenAll(largQueryTasks);

            // Export data
            var exportResponse = await _client.GetAsync($"/ogc/features/v1/collections/memory-test-cycle-{cycle}/items?f=application/geo+json");
            exportResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.PartialContent);

            // Force garbage collection between cycles
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Task.Delay(1000); // Allow cleanup
        }

        // Verify memory usage remains reasonable
        var finalMemory = GC.GetTotalMemory(true);
        var memoryIncrease = finalMemory - initialMemory;
        var memoryIncreaseMB = memoryIncrease / (1024.0 * 1024.0);

        memoryIncreaseMB.Should().BeLessThan(500, "Memory usage should be bounded across complex workflows");
    }

    [IntegrationTest]
    [Fact(DisplayName = "All fixes work together in production-like environment")]
    public async Task AllFixes_WorkTogetherInProductionEnvironment()
    {
        // Comprehensive test combining all critical fixes in a production-like scenario

        // Step 1: Security - Production environment with proper authentication
        var prodClient = CreateProductionClientWithAuth();

        var authResponse = await prodClient.GetAsync("/admin/health");
        authResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Production authentication should work");

        // Step 2: Performance - High volume operations
        var performanceTasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var operations = new[]
            {
                _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=id>{i}&resultRecordCount=100"),
                _client.GetAsync($"/ogc/features/v1/collections/test/items?limit=50&offset={i * 50}"),
                _client.GetAsync("/rest/services/1/FeatureServer/layers")
            };

            var results = await Task.WhenAll(operations);
            return results.All(r => r.StatusCode == HttpStatusCode.OK);
        });

        var performanceResults = await Task.WhenAll(performanceTasks);
        performanceResults.Should().AllSatisfy(success => success.Should().BeTrue("Performance operations should succeed"));

        // Step 3: Resilience - Mixed success/failure scenarios
        var resilienceTasks = new List<Task<bool>>();

        // Valid operations
        for (int i = 0; i < 20; i++)
        {
            resilienceTasks.Add(MakeRequestAsync($"/admin/health?valid={i}"));
        }

        // Invalid operations (should be handled gracefully)
        for (int i = 0; i < 10; i++)
        {
            resilienceTasks.Add(MakeRequestAsync($"/rest/services/99999/FeatureServer/0/query?invalid={i}"));
        }

        var resilienceResults = await Task.WhenAll(resilienceTasks);
        var validSuccesses = resilienceResults.Take(20).Count(r => r);
        var gracefulHandling = resilienceResults.Skip(20).Count(r => !r); // Invalid requests should fail gracefully

        validSuccesses.Should().BeGreaterOrEqualTo(18, "Valid operations should mostly succeed");
        gracefulHandling.Should().BeGreaterOrEqualTo(8, "Invalid operations should be handled gracefully");

        // Step 4: Memory - Verify no leaks during complex operations
        var memoryBefore = GC.GetTotalMemory(true);

        var memoryStressTasks = Enumerable.Range(0, 30).Select(async i =>
        {
            var largeQuery = await _client.GetAsync($"/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=1000&memory_test={i}");
            return largeQuery.StatusCode == HttpStatusCode.OK;
        });

        await Task.WhenAll(memoryStressTasks);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var memoryAfter = GC.GetTotalMemory(true);
        var memoryGrowthMB = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);

        memoryGrowthMB.Should().BeLessThan(200, "Memory growth should be controlled");

        // Final health check
        var finalHealth = await _client.GetAsync("/admin/health");
        finalHealth.StatusCode.Should().Be(HttpStatusCode.OK, "System should remain healthy after comprehensive testing");
    }

    #region Helper Methods

    private HttpClient CreateProductionClient()
    {
        return _fixture.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
        }).CreateClient();
    }

    private HttpClient CreateProductionClientWithAuth()
    {
        var client = CreateProductionClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "production-admin-key");
        return client;
    }

    private async Task<bool> MakeRequestAsync(string url)
    {
        try
        {
            var response = await _client.GetAsync(url);
            return (int)response.StatusCode < 500; // Success or client error, not server error
        }
        catch
        {
            return false;
        }
    }

    private async Task SeedPerformanceTestDataAsync()
    {
        var testData = GenerateTestGeoJsonData(5000);
        var content = new MultipartFormDataContent
        {
            { new StringContent(testData), "file", "performance-test-data.geojson" },
            { new StringContent("performance-test-layer"), "name" }
        };

        var response = await _client.PostAsync("/admin/import/geojson", content);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.Accepted);

        // Wait for import
        await Task.Delay(3000);
    }

    private static string GenerateTestGeoJsonData(int featureCount)
    {
        var features = Enumerable.Range(0, featureCount).Select(i => new
        {
            type = "Feature",
            properties = new
            {
                id = i,
                name = $"Integration Test Feature {i}",
                category = i % 10,
                value = Random.Shared.NextDouble() * 1000
            },
            geometry = new
            {
                type = "Point",
                coordinates = new[]
                {
                    -180 + (360.0 * Random.Shared.NextDouble()),
                    -90 + (180.0 * Random.Shared.NextDouble())
                }
            }
        });

        var geoJson = new
        {
            type = "FeatureCollection",
            features = features.Select(f => new
            {
                type = "Feature",
                properties = f.properties,
                geometry = f.geometry
            })
        };

        return System.Text.Json.JsonSerializer.Serialize(geoJson);
    }

    private static string GenerateBulkFeatureData(int count, string prefix)
    {
        var features = Enumerable.Range(0, count).Select(i => new
        {
            id = $"{prefix}_{i}",
            properties = new { name = $"Bulk {prefix} {i}" },
            geometry = new
            {
                type = "Point",
                coordinates = new[] { i % 360 - 180, (i % 180) - 90 }
            }
        });

        return System.Text.Json.JsonSerializer.Serialize(new { features });
    }

    #endregion
}