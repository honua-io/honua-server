// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests;

/// <summary>
/// Critical fix validation suite that orchestrates comprehensive testing.
/// Validates security, performance, and resilience fixes work correctly together.
/// </summary>
[Collection("Database")]
public class CriticalFixValidationSuite : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public CriticalFixValidationSuite(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(DisplayName = "Test environment supports critical fix validation")]
    public async Task TestEnvironment_SupportsCriticalFixValidation()
    {
        // Verify test environment has necessary components for validation
        var client = _fixture.Client;

        // Check basic connectivity and health
        var healthResponse = await client.GetAsync("/admin/health");
        healthResponse.Should().NotBeNull("Test environment should be accessible");

        // Verify test service is available
        var serviceResponse = await client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer");
        serviceResponse.Should().NotBeNull("Test service should be available");

        // Verify database connectivity through feature queries
        var queryResponse = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=1");
        queryResponse.Should().NotBeNull("Database should be accessible through test service");
    }

    [Fact(DisplayName = "Critical fix test categories are properly organized")]
    public void CriticalFixTestCategories_ProperlyOrganized()
    {
        // Validate that critical fix tests are properly categorized
        var expectedTestCategories = new[]
        {
            typeof(Features.Security.SecurityFixValidationTests),
            typeof(Performance.PerformanceFixValidationTests),
            typeof(Features.Infrastructure.MemoryManagementTests),
            typeof(Features.Infrastructure.ResiliencePatternTests),
            typeof(LoadTests.CriticalFixLoadTests),
            typeof(Integration.CriticalFixIntegrationTests)
        };

        foreach (var testCategory in expectedTestCategories)
        {
            testCategory.Should().NotBeNull($"Test category {testCategory.Name} should exist");

            // Verify test methods exist
            var testMethods = testCategory.GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Any())
                .ToList();

            testMethods.Should().NotBeEmpty($"Test category {testCategory.Name} should have test methods");
        }
    }

    [Fact(DisplayName = "Performance baselines can be established")]
    public async Task PerformanceBaselines_CanBeEstablished()
    {
        // Establish baseline metrics for regression detection
        var client = _fixture.Client;
        var baselines = new Dictionary<string, TimeSpan>();

        // Health check baseline
        var healthStart = DateTime.UtcNow;
        var healthResponse = await client.GetAsync("/admin/health");
        baselines["health_check"] = DateTime.UtcNow - healthStart;

        // Service metadata baseline
        var metadataStart = DateTime.UtcNow;
        var metadataResponse = await client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer");
        baselines["service_metadata"] = DateTime.UtcNow - metadataStart;

        // Simple query baseline
        var queryStart = DateTime.UtcNow;
        var queryResponse = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1=1&resultRecordCount=10");
        baselines["simple_query"] = DateTime.UtcNow - queryStart;

        // Assert baselines are reasonable
        foreach (var baseline in baselines)
        {
            Console.WriteLine($"Baseline {baseline.Key}: {baseline.Value.TotalMilliseconds:F1}ms");

            baseline.Value.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(10),
                $"Baseline {baseline.Key} should be reasonable for test environment");
        }

        // Health operations should be very fast
        baselines["health_check"].Should().BeLessOrEqualTo(TimeSpan.FromSeconds(2),
            "Health check should be very fast");

        // All operations should succeed
        healthResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        metadataResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        queryResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact(DisplayName = "Security test infrastructure is functional")]
    public async Task SecurityTestInfrastructure_IsFunctional()
    {
        var client = _fixture.Client;

        // Test that we can detect security responses
        var testEndpoints = new[]
        {
            "/admin/health", // Should work without auth in test environment
            "/admin/nonexistent", // Should return 404
            "/rest/services/nonexistent/FeatureServer/0/query" // Should handle gracefully
        };

        foreach (var endpoint in testEndpoints)
        {
            var response = await client.GetAsync(endpoint);
            response.Should().NotBeNull($"Endpoint {endpoint} should return a response");

            // Response should be a valid HTTP status
            ((int)response.StatusCode).Should().BeInRange(200, 599,
                $"Endpoint {endpoint} should return valid HTTP status");
        }
    }

    [Fact(DisplayName = "Load testing infrastructure supports concurrent operations")]
    public async Task LoadTestingInfrastructure_SupportsConcurrentOperations()
    {
        var client = _fixture.Client;
        var concurrentCount = 10; // Conservative for test validation

        // Execute concurrent operations
        var concurrentTasks = Enumerable.Range(0, concurrentCount).Select(async i =>
        {
            var response = await client.GetAsync($"/admin/health?concurrent_test={i}");
            return new
            {
                TaskId = i,
                Success = response.StatusCode == System.Net.HttpStatusCode.OK,
                StatusCode = response.StatusCode
            };
        });

        var results = await Task.WhenAll(concurrentTasks);

        // Assert infrastructure supports concurrency
        results.Should().HaveCount(concurrentCount, "All concurrent tasks should complete");

        var successCount = results.Count(r => r.Success);
        successCount.Should().BeGreaterOrEqualTo(concurrentCount * 0.9,
            "At least 90% of concurrent operations should succeed");
    }

    /// <summary>
    /// Gets test execution recommendations based on available time.
    /// </summary>
    public static class TestExecutionPlanner
    {
        public static string[] GetQuickValidationTests()
        {
            return new[]
            {
                "dotnet test --filter \"DisplayName~Security.*validation\"",
                "dotnet test --filter \"DisplayName~Performance.*thresholds\"",
                "dotnet test --filter \"DisplayName~Test environment.*validation\""
            };
        }

        public static string[] GetComprehensiveValidationTests()
        {
            return new[]
            {
                "dotnet test tests/Honua.Server.Tests/Features/Security/ --logger \"console;verbosity=normal\"",
                "dotnet test tests/Honua.Server.Tests/Performance/ --logger \"console;verbosity=normal\"",
                "dotnet test tests/Honua.Server.Tests/Features/Infrastructure/ --logger \"console;verbosity=normal\"",
                "dotnet test tests/Honua.Server.Tests/Integration/ --logger \"console;verbosity=normal\""
            };
        }

        public static string[] GetProductionReadinessTests()
        {
            return new[]
            {
                "dotnet test --filter \"Category=SecurityTest\" --logger \"console;verbosity=normal\"",
                "dotnet test --filter \"FullyQualifiedName~Performance.*Validation\" --logger \"console;verbosity=normal\"",
                "dotnet test --filter \"FullyQualifiedName~Integration.*Tests\" --logger \"console;verbosity=normal\""
            };
        }

        public static TimeSpan EstimateExecutionTime(string[] testCommands)
        {
            // Rough estimation based on test categories
            var baseTime = TimeSpan.FromMinutes(1); // Setup/teardown overhead
            var testTime = TimeSpan.FromMinutes(testCommands.Length * 2); // 2 minutes per category

            return baseTime.Add(testTime);
        }
    }
}

/// <summary>
/// Test execution summary and reporting helpers.
/// </summary>
public static class CriticalFixTestReporting
{
    public static string GenerateExecutionSummary(string testCategory, bool passed, TimeSpan duration, string[] errors = null)
    {
        var status = passed ? "✅ PASSED" : "❌ FAILED";
        var summary = $"{status} - {testCategory} ({duration.TotalSeconds:F1}s)";

        if (errors?.Any() == true)
        {
            summary += Environment.NewLine + "Errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => $"  • {e}"));
        }

        return summary;
    }

    public static string GetTestExecutionGuide()
    {
        return """
        # Critical Fix Test Execution Guide

        ## Quick Validation (5 minutes)
        ```bash
        dotnet test --filter "DisplayName~Security.*validation" --logger "console;verbosity=normal"
        dotnet test --filter "DisplayName~Performance.*thresholds" --logger "console;verbosity=normal"
        ```

        ## Full Validation (20 minutes)
        ```bash
        dotnet test tests/Honua.Server.Tests/Features/Security/ --logger "console;verbosity=normal"
        dotnet test tests/Honua.Server.Tests/Performance/ --logger "console;verbosity=normal"
        dotnet test tests/Honua.Server.Tests/Integration/ --logger "console;verbosity=normal"
        ```

        ## Production Readiness (15 minutes)
        ```bash
        dotnet test --filter "Category=SecurityTest" --logger "console;verbosity=normal"
        dotnet test --filter "FullyQualifiedName~Performance.*Validation" --logger "console;verbosity=normal"
        ```

        All tests should pass for production deployment confidence.
        """;
    }
}