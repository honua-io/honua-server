// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests that demonstrate the enhanced telemetry features working together
/// with smart sampling rules and adaptive sampling.
/// </summary>
public class EnhancedTelemetryIntegrationTests
{
    [Fact]
    public void SmartSamplingRules_WithCriticalOperation_AlwaysSamples()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        var context = new SamplingContext
        {
            OperationName = "auth.login",
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Critical operation", decision.Reason);
        Assert.Equal(1.0, decision.ConfidenceScore);
    }

    [Fact]
    public void SmartSamplingRules_WithVipClient_AlwaysSamples()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var vipClients = new[] { "vip-client-123" };
        var samplingRules = new SmartSamplingRules(logger, vipClients);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            ClientId = "vip-client-123",
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("VIP client operation", decision.Reason);
        Assert.Equal(0.95, decision.ConfidenceScore);
    }

    [Fact]
    public void SmartSamplingRules_WithLargeOperation_AlwaysSamples()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            FeatureCount = 15000, // Above threshold of 10,000
            DurationMs = 100
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Large operation (high feature count)", decision.Reason);
        Assert.Equal(0.9, decision.ConfidenceScore);
    }

    [Fact]
    public void SmartSamplingRules_WithSlowOperation_AlwaysSamples()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            DurationMs = 6000, // Above threshold of 5,000ms
            FeatureCount = 10
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Slow operation detected", decision.Reason);
        Assert.Equal(0.9, decision.ConfidenceScore);
    }

    [Fact]
    public void SmartSamplingRules_WithErrorCondition_AlwaysSamples()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            Exception = new InvalidOperationException("Test error"),
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Error condition detected", decision.Reason);
        Assert.Equal(1.0, decision.ConfidenceScore);
    }

    [Fact]
    public void SmartSamplingRules_WithRegularOperation_DefersToAdaptive()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Null(decision.ShouldSample);
        Assert.Contains("No smart rules matched", decision.Reason);
        Assert.Equal(0.0, decision.ConfidenceScore);
    }

    [Theory]
    [InlineData("auth.login", "Critical operation")]
    [InlineData("user.create", "Critical operation")]
    [InlineData("data.update", "Critical operation")]
    [InlineData("security.validate", "Critical operation")]
    [InlineData("spatial.buffer", "Geospatially complex operation")]
    [InlineData("experimental.feature", "New or untested code path")]
    public void SmartSamplingRules_ClassifiesOperationsCorrectly(string operationName, string expectedReason)
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        var context = new SamplingContext
        {
            OperationName = operationName,
            DurationMs = 100,
            FeatureCount = 10,
            GeospatialComplexity = operationName.Contains("spatial") ? 2000 : 0
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason);
        Assert.True(decision.ConfidenceScore > 0);
    }

    [Fact]
    public void SmartSamplingRules_PerformanceBaseline_WorksCorrectly()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        // Set baseline for operation
        samplingRules.UpdatePerformanceBaseline("feature.query", 200.0);

        // Test operation that exceeds baseline by 60% (> 50% threshold)
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            DurationMs = 320.0, // 60% above baseline
            FeatureCount = 10
        };

        // Act
        var decision = samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Performance baseline exceeded", decision.Reason);
        Assert.Equal(0.7, decision.ConfidenceScore);
    }

    [Fact]
    public void SmartSamplingRules_VipClientManagement_WorksCorrectly()
    {
        // Arrange
        var logger = NullLogger<SmartSamplingRules>.Instance;
        var samplingRules = new SmartSamplingRules(logger);

        const string testClient = "test-vip-client";

        // Add VIP client
        samplingRules.AddVipClient(testClient);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            ClientId = testClient,
            DurationMs = 100,
            FeatureCount = 10
        };

        // Test that client is now VIP
        var decision = samplingRules.EvaluateSampling(context);
        Assert.True(decision.ShouldSample);
        Assert.Equal("VIP client operation", decision.Reason);

        // Remove VIP client
        samplingRules.RemoveVipClient(testClient);

        // Test that client is no longer VIP
        decision = samplingRules.EvaluateSampling(context);
        Assert.Null(decision.ShouldSample);
        Assert.Contains("No smart rules matched", decision.Reason);
    }
}
