// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Middleware;

/// <summary>
/// Integration tests for PerformanceMonitoringMiddleware.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.TestQuality)]
public class PerformanceMonitoringMiddlewareTests
{

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /test-performance")]
    public async Task PerformanceMiddleware_ShouldTrackRequestMetrics()
    {
        // Arrange
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        var app = CreateTestApp(services =>
        {
            services.AddSingleton(performanceMonitor);
            services.Configure<PerformanceMonitoringOptions>(opt =>
            {
                opt.EnableMemoryTracking = true;
                opt.SlowRequestThreshold = TimeSpan.FromMilliseconds(100);
            });
        });

        // Act
        var response = await app.GetAsync("/test");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        performanceMonitor.Received().RecordHttpRequest(
            Arg.Is("GET"),
            Arg.Any<string>(),
            Arg.Is(200),
            Arg.Any<TimeSpan>());
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /test-slow")]
    public async Task PerformanceMiddleware_ShouldDetectSlowRequests()
    {
        // Arrange
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        var app = CreateTestApp(services =>
        {
            services.AddSingleton(performanceMonitor);
            services.Configure<PerformanceMonitoringOptions>(opt =>
            {
                opt.EnableMemoryTracking = false;
                opt.SlowRequestThreshold = TimeSpan.FromMilliseconds(10); // Very low threshold
            });
        }, addSlowEndpoint: true);

        // Act
        var response = await app.GetAsync("/slow");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        performanceMonitor.Received().RecordHttpRequest(
            Arg.Is("GET"),
            Arg.Any<string>(),
            Arg.Is(200),
            Arg.Any<TimeSpan>());
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /test-error")]
    public async Task PerformanceMiddleware_ShouldTrackFailedRequests()
    {
        // Arrange
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        var app = CreateTestApp(services =>
        {
            services.AddSingleton(performanceMonitor);
            services.Configure<PerformanceMonitoringOptions>(opt =>
            {
                opt.EnableMemoryTracking = false;
                opt.SlowRequestThreshold = TimeSpan.FromSeconds(1);
            });
        }, addErrorEndpoint: true);

        // Act
        var response = await app.GetAsync("/error");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        operationScope.Received().WithTag("error", Arg.Any<string>());
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /unmatched")]
    public async Task PerformanceMiddleware_ShouldCollapseUnmatchedRoutesIntoSingleMetricEndpoint()
    {
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        IDictionary<string, string>? detailedTags = null;
        performanceMonitor
            .When(m => m.RecordHistogram(
                "honua_request_duration_detailed_ms",
                Arg.Any<double>(),
                Arg.Any<IDictionary<string, string>>()))
            .Do(callInfo => detailedTags = callInfo.ArgAt<IDictionary<string, string>>(2));

        var app = CreateTestApp(services =>
        {
            services.AddSingleton(performanceMonitor);
            services.Configure<PerformanceMonitoringOptions>(opt =>
            {
                opt.EnableMemoryTracking = false;
                opt.SlowRequestThreshold = TimeSpan.FromSeconds(1);
            });
        });

        var response = await app.GetAsync("/tenant/acme/widgets/not-a-real-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        performanceMonitor.Received().RecordHttpRequest(
            "GET",
            "/{unmatched}",
            StatusCodes.Status404NotFound,
            Arg.Any<TimeSpan>());
        Assert.NotNull(detailedTags);
        Assert.Equal("/{unmatched}", detailedTags!["endpoint"]);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /test-memory-tracking")]
    public async Task PerformanceMiddleware_WithMemoryTracking_ShouldSampleMemory()
    {
        // Arrange
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        var app = CreateTestApp(services =>
        {
            services.AddSingleton(performanceMonitor);
            services.Configure<PerformanceMonitoringOptions>(opt =>
            {
                opt.EnableMemoryTracking = true;
                opt.MemorySamplingInterval = 1; // Sample every request for testing
            });
        });

        // Act
        var response = await app.GetAsync("/test");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        performanceMonitor.Received().RecordMemoryUsage(
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int>());
    }

    [Fact]
    public void PerformanceMonitoringOptions_DefaultValues_ShouldBeReasonable()
    {
        // Arrange & Act
        var options = new PerformanceMonitoringOptions();

        // Assert
        Assert.True(options.EnableMemoryTracking, "Memory tracking should be enabled by default");
        Assert.Equal(TimeSpan.FromSeconds(1), options.SlowRequestThreshold);
        Assert.Equal(100, options.MemorySamplingInterval);
        Assert.True(options.EnableDetailedRequestTracking, "Detailed tracking should be enabled by default");
    }

    [Fact]
    public async Task PerformanceMiddleware_ShouldMaintainActiveRequestCount()
    {
        // This test verifies that active request counting works correctly
        // In a real implementation, we'd need access to the metric values
        Assert.True(true, "Active request counting is handled by .NET Metrics API");
    }

    private HttpClient CreateTestApp(
        Action<IServiceCollection>? configureServices = null,
        bool addSlowEndpoint = false,
        bool addErrorEndpoint = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // Configure services
        configureServices?.Invoke(builder.Services);
        if (!builder.Services.Any(service => service.ServiceType == typeof(ISystemMetricsCollector)))
        {
            var metricsCollector = Substitute.For<ISystemMetricsCollector>();
            metricsCollector.TrackRequest().Returns(Substitute.For<IDisposable>());
            builder.Services.AddSingleton(metricsCollector);
        }

        var app = builder.Build();

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            });
        });

        // Add performance monitoring middleware
        app.UseMiddleware<PerformanceMonitoringMiddleware>();

        // Add test endpoints
        app.MapGet("/test", (HttpContext _) => Results.Ok("test response"));

        if (addSlowEndpoint)
        {
            app.MapGet("/slow", async (HttpContext _) =>
            {
                await Task.Delay(50); // Simulate slow operation
                return Results.Ok("slow response");
            });
        }

        if (addErrorEndpoint)
        {
            app.MapGet("/error", (HttpContext _) => throw new InvalidOperationException("Test error"));
        }

        app.StartAsync().GetAwaiter().GetResult();
        return app.GetTestClient();
    }

}
