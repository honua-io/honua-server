// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Tests for smart sampling rules functionality including business logic
/// for critical operations, error conditions, and performance characteristics.
/// </summary>
public class SmartSamplingRulesTests
{
    private static readonly string[] _vipClients = ["vip-client-1", "premium-user"];
    private readonly ILogger<SmartSamplingRules> _logger;
    private readonly SmartSamplingRules _samplingRules;

    public SmartSamplingRulesTests()
    {
        _logger = NullLogger<SmartSamplingRules>.Instance;
        _samplingRules = new SmartSamplingRules(_logger, _vipClients);
    }

    [Theory]
    [InlineData("auth.login", true, "Critical operation")]
    [InlineData("user.create", true, "Critical operation")]
    [InlineData("data.update", true, "Critical operation")]
    [InlineData("record.delete", true, "Critical operation")]
    [InlineData("security.validate", true, "Critical operation")]
    [InlineData("transaction.process", true, "Critical operation")]
    [InlineData("feature.query", null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_CriticalOperations_ReturnsExpectedDecision(
        string operationName, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = operationName,
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);

        if (expectedSample.HasValue)
        {
            Assert.True(decision.ConfidenceScore > 0);
        }
    }

    [Fact]
    public void EvaluateSampling_ErrorCondition_ReturnsAlwaysSample()
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            Exception = new InvalidOperationException("Test error"),
            DurationMs = 100
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Error condition detected", decision.Reason);
        Assert.Equal(1.0, decision.ConfidenceScore);
    }

    [Fact]
    public void EvaluateSampling_HttpErrorStatus_ReturnsAlwaysSample()
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            HttpStatusCode = 500,
            DurationMs = 100
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Error condition detected", decision.Reason);
        Assert.Equal(1.0, decision.ConfidenceScore);
    }

    [Theory]
    [InlineData(15000, true, "Large operation (high feature count)")]
    [InlineData(20 * 1024 * 1024, true, "Large operation (high feature count)")] // 20MB data
    [InlineData(1000, null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_LargeOperations_ReturnsExpectedDecision(
        long value, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            FeatureCount = value < 100000 ? (int)value : 100,
            DataSizeBytes = value >= 100000 ? value : 1024,
            DurationMs = 100
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(6000, true, "Slow operation detected")]
    [InlineData(1000, null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_SlowOperations_ReturnsExpectedDecision(
        double durationMs, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            DurationMs = durationMs,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("vip-client-1", true, "VIP client operation")]
    [InlineData("premium-user", true, "VIP client operation")]
    [InlineData("regular-user", null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_VipClients_ReturnsExpectedDecision(
        string clientId, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            ClientId = clientId,
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("spatial.buffer", 2000, 3, true, true, "Geospatially complex operation")]
    [InlineData("geometry.union", 500, 2, false, null, "No smart rules matched, defer to adaptive sampling")]
    [InlineData("feature.query", 100, 4, false, true, "Geospatially complex operation")] // 4D operation
    public void EvaluateSampling_GeospatialComplexity_ReturnsExpectedDecision(
        string operationName, int geospatialComplexity, int spatialDimensions,
        bool requiresHighPrecision, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = operationName,
            DurationMs = 100,
            GeospatialComplexity = geospatialComplexity,
            SpatialDimensions = spatialDimensions,
            RequiresHighPrecision = requiresHighPrecision,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateSampling_PerformanceBaselineExceeded_ReturnsAlwaysSample()
    {
        // Arrange
        _samplingRules.UpdatePerformanceBaseline("feature.query", 200.0);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            DurationMs = 350.0, // 75% above baseline
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Performance baseline exceeded", decision.Reason);
        Assert.Equal(0.7, decision.ConfidenceScore);
    }

    [Theory]
    [InlineData("personal.data.access", null, true, "Security-sensitive operation")]
    [InlineData("user.profile.update", null, true, "Security-sensitive operation")]
    [InlineData("feature.query", "high", true, "Security-sensitive operation")]
    [InlineData("feature.query", "low", null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_SecuritySensitive_ReturnsExpectedDecision(
        string operationName, string? riskLevel, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = operationName,
            SecurityRiskLevel = riskLevel,
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("experimental.feature", true, "New or untested code path")]
    [InlineData("beta.endpoint", true, "New or untested code path")]
    [InlineData("stable.feature", null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_NewCodePath_ReturnsExpectedDecision(
        string operationName, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = operationName,
            DurationMs = 100,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(85, 90, 1200, 95, 80, true, "High business value under system stress")]
    [InlineData(85, 90, 1200, 95, 30, false, "System under stress, low business value")]
    [InlineData(50, 60, 500, 70, 50, null, "No smart rules matched, defer to adaptive sampling")]
    public void EvaluateSampling_SystemStress_ReturnsExpectedDecision(
        double cpuUsage, double memoryUsage, int activeRequests, double dbPoolUsage,
        int businessValue, bool? expectedSample, string expectedReason)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = "feature.query",
            DurationMs = 100,
            SystemCpuUsage = cpuUsage,
            SystemMemoryUsage = memoryUsage,
            ActiveRequests = activeRequests,
            DatabaseConnectionPoolUsage = dbPoolUsage,
            BusinessValue = businessValue,
            FeatureCount = 10
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.Equal(expectedSample, decision.ShouldSample);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdatePerformanceBaseline_ValidInput_UpdatesBaseline()
    {
        // Arrange
        const string operationName = "test.operation";
        const double baselineDuration = 150.0;

        // Act
        _samplingRules.UpdatePerformanceBaseline(operationName, baselineDuration);

        // Create context that exceeds baseline by 60%
        var context = new SamplingContext
        {
            OperationName = operationName,
            DurationMs = 240.0, // 60% above baseline
            FeatureCount = 10
        };

        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("Performance baseline exceeded", decision.Reason);
    }

    [Fact]
    public void AddVipClient_ValidClientId_AddsToVipList()
    {
        // Arrange
        var newRules = new SmartSamplingRules(_logger);
        const string clientId = "new-vip-client";

        // Act
        newRules.AddVipClient(clientId);

        var context = new SamplingContext
        {
            OperationName = "feature.query",
            ClientId = clientId,
            DurationMs = 100,
            FeatureCount = 10
        };

        var decision = newRules.EvaluateSampling(context);

        // Assert
        Assert.True(decision.ShouldSample);
        Assert.Equal("VIP client operation", decision.Reason);
    }

    [Fact]
    public void RemoveVipClient_ExistingClient_RemovesFromVipList()
    {
        // Arrange
        const string clientId = "vip-client-1";

        // Verify client is initially VIP
        var contextBefore = new SamplingContext
        {
            OperationName = "feature.query",
            ClientId = clientId,
            DurationMs = 100,
            FeatureCount = 10
        };

        var decisionBefore = _samplingRules.EvaluateSampling(contextBefore);
        Assert.True(decisionBefore.ShouldSample);
        Assert.Equal("VIP client operation", decisionBefore.Reason);

        // Act
        _samplingRules.RemoveVipClient(clientId);

        // Verify client is no longer VIP
        var contextAfter = new SamplingContext
        {
            OperationName = "feature.query",
            ClientId = clientId,
            DurationMs = 100,
            FeatureCount = 10
        };

        var decisionAfter = _samplingRules.EvaluateSampling(contextAfter);

        // Assert
        Assert.Null(decisionAfter.ShouldSample);
        Assert.Contains("No smart rules matched", decisionAfter.Reason);
    }

    [Fact]
    public void EvaluateSampling_NullContext_ReturnsNoSample()
    {
        // Act
        var decision = _samplingRules.EvaluateSampling(null!);

        // Assert
        Assert.False(decision.ShouldSample);
        Assert.Equal("No context provided", decision.Reason);
        Assert.Equal(0.0, decision.ConfidenceScore);
    }

    [Theory]
    [InlineData("bulk.import", true)]
    [InlineData("batch.process", true)]
    [InlineData("mass.update", true)]
    [InlineData("import.data", true)]
    [InlineData("regular.operation", false)]
    public void EvaluateSampling_BulkOperationNames_IdentifiedCorrectly(string operationName, bool expectedBulk)
    {
        // Arrange
        var context = new SamplingContext
        {
            OperationName = operationName,
            DurationMs = 100,
            FeatureCount = 100
        };

        // Act
        var decision = _samplingRules.EvaluateSampling(context);

        // Assert
        if (expectedBulk)
        {
            Assert.True(decision.ShouldSample);
            Assert.Equal("Large operation (high feature count)", decision.Reason);
        }
        else
        {
            Assert.Null(decision.ShouldSample);
        }
    }
}
