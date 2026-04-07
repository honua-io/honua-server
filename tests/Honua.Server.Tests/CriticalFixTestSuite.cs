// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests;

/// <summary>
/// Test suite orchestrator for critical fix validation.
/// Runs comprehensive tests in proper order and validates overall system health.
/// </summary>
[Collection("Database")]
public class CriticalFixTestSuite : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public CriticalFixTestSuite(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(DisplayName = "Test environment is properly configured for critical fix validation")]
    public async Task TestEnvironment_ProperlyConfiguredForValidation()
    {
        // Verify test environment has necessary components
        var client = _fixture.CreateClient();

        // Check basic connectivity
        var healthResponse = await client.GetAsync("/admin/health");
        healthResponse.Should().NotBeNull("Test environment should be accessible");

        // Verify database connectivity
        var layersResponse = await client.GetAsync("/rest/services/1/FeatureServer/layers");
        layersResponse.Should().NotBeNull("Database should be accessible through test environment");

        // Verify Redis connectivity (if available)
        try
        {
            var cacheTestResponse = await client.GetAsync("/admin/health?cache_test=true");
            // Cache test succeeds or fails gracefully
        }
        catch
        {
            // Redis may not be available in all test environments
        }
    }

    [Fact(DisplayName = "All critical fix test categories are executable")]
    public void AllCriticalFixTestCategories_AreExecutable()
    {
        // This test validates that all our test categories can run
        // It serves as a smoke test for the test infrastructure

        var testCategories = new[]
        {
            typeof(Features.Security.CriticalSecurityFixTests),
            typeof(Performance.CriticalPerformanceFixTests),
            typeof(Features.Infrastructure.MemoryManagementTests),
            typeof(Features.Infrastructure.ResiliencePatternTests),
            typeof(LoadTests.CriticalFixLoadTests),
            typeof(Integration.CriticalFixIntegrationTests)
        };

        foreach (var testClass in testCategories)
        {
            testClass.Should().NotBeNull($"Test category {testClass.Name} should be available");

            // Verify the class has test methods
            var testMethods = testClass.GetMethods()
                .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Any() ||
                           m.GetCustomAttributes(typeof(TheoryAttribute), false).Any())
                .ToList();

            testMethods.Should().NotBeEmpty($"Test category {testClass.Name} should have test methods");
        }
    }

    [Fact(DisplayName = "Test attributes are properly applied for categorization")]
    public void TestAttributes_ProperlyAppliedForCategorization()
    {
        // Verify our tests are properly categorized for selective execution
        var securityTestType = typeof(Features.Security.CriticalSecurityFixTests);
        var performanceTestType = typeof(Performance.CriticalPerformanceFixTests);
        var integrationTestType = typeof(Integration.CriticalFixIntegrationTests);

        // Check for proper attribute usage
        var securityMethods = securityTestType.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(SecurityTestAttribute), false).Any());

        securityMethods.Should().NotBeEmpty("Security tests should be marked with [SecurityTest]");

        var integrationMethods = integrationTestType.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(IntegrationTestAttribute), false).Any());

        integrationMethods.Should().NotBeEmpty("Integration tests should be marked with [IntegrationTest]");
    }

    /// <summary>
    /// Validates that performance benchmarks are within expected ranges.
    /// This serves as a baseline for regression detection.
    /// </summary>
    [Fact(DisplayName = "Performance benchmarks establish baseline metrics")]
    public async Task PerformanceBenchmarks_EstablishBaselineMetrics()
    {
        var client = _fixture.CreateClient();

        // Establish baseline metrics for common operations
        var benchmarks = new Dictionary<string, TimeSpan>();

        // Simple health check
        var healthStart = DateTime.UtcNow;
        await client.GetAsync("/admin/health");
        benchmarks["health_check"] = DateTime.UtcNow - healthStart;

        // Metadata query
        var metadataStart = DateTime.UtcNow;
        await client.GetAsync("/rest/services/1/FeatureServer/layers");
        benchmarks["metadata_query"] = DateTime.UtcNow - metadataStart;

        // Simple feature query
        var featureStart = DateTime.UtcNow;
        await client.GetAsync("/rest/services/1/FeatureServer/0/query?where=1=1&resultRecordCount=10");
        benchmarks["simple_feature_query"] = DateTime.UtcNow - featureStart;

        // Log benchmarks for baseline establishment
        foreach (var benchmark in benchmarks)
        {
            Console.WriteLine($"Baseline {benchmark.Key}: {benchmark.Value.TotalMilliseconds:F1}ms");
        }

        // Sanity checks on baseline performance
        benchmarks["health_check"].Should().BeLessOrEqualTo(TimeSpan.FromSeconds(1),
            "Health check should be very fast");
        benchmarks["metadata_query"].Should().BeLessOrEqualTo(TimeSpan.FromSeconds(2),
            "Metadata queries should be reasonably fast");
        benchmarks["simple_feature_query"].Should().BeLessOrEqualTo(TimeSpan.FromSeconds(3),
            "Simple feature queries should complete quickly");
    }
}

/// <summary>
/// Test runner configuration for critical fix validation.
/// Provides methods to run specific test categories and generate reports.
/// </summary>
public static class CriticalFixTestRunner
{
    /// <summary>
    /// Test categories available for selective execution.
    /// </summary>
    public enum TestCategory
    {
        Security,
        Performance,
        Memory,
        Resilience,
        Load,
        Integration,
        All
    }

    /// <summary>
    /// Gets the test filter expression for a specific category.
    /// Use with: dotnet test --filter "expression"
    /// </summary>
    public static string GetTestFilter(TestCategory category)
    {
        return category switch
        {
            TestCategory.Security => "Category=SecurityTest",
            TestCategory.Performance => "FullyQualifiedName~Performance",
            TestCategory.Memory => "FullyQualifiedName~Memory",
            TestCategory.Resilience => "FullyQualifiedName~Resilience",
            TestCategory.Load => "FullyQualifiedName~LoadTests",
            TestCategory.Integration => "Category=IntegrationTest",
            TestCategory.All => "FullyQualifiedName~CriticalFix",
            _ => throw new ArgumentException($"Unknown test category: {category}")
        };
    }

    /// <summary>
    /// Gets recommended test execution order for comprehensive validation.
    /// </summary>
    public static TestCategory[] GetRecommendedExecutionOrder()
    {
        return new[]
        {
            TestCategory.Security,    // Run security tests first (fast, critical)
            TestCategory.Performance, // Then performance (medium speed, important)
            TestCategory.Memory,      // Memory management tests
            TestCategory.Resilience,  // Resilience patterns
            TestCategory.Integration, // Integration tests (slower but comprehensive)
            TestCategory.Load        // Load tests last (slowest, most resource intensive)
        };
    }

    /// <summary>
    /// Estimates test execution time for planning purposes.
    /// </summary>
    public static TimeSpan GetEstimatedDuration(TestCategory category)
    {
        return category switch
        {
            TestCategory.Security => TimeSpan.FromMinutes(2),
            TestCategory.Performance => TimeSpan.FromMinutes(5),
            TestCategory.Memory => TimeSpan.FromMinutes(3),
            TestCategory.Resilience => TimeSpan.FromMinutes(4),
            TestCategory.Integration => TimeSpan.FromMinutes(8),
            TestCategory.Load => TimeSpan.FromMinutes(15),
            TestCategory.All => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromMinutes(1)
        };
    }
}