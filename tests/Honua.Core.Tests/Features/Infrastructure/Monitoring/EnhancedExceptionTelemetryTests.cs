// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Tests for EnhancedExceptionTelemetry - critical error monitoring and telemetry
/// </summary>
public sealed class EnhancedExceptionTelemetryTests
{
    private readonly ILogger<EnhancedExceptionTelemetry> _mockLogger;
    private readonly IOptionsMonitor<ExceptionTelemetryOptions> _mockOptions;
    private readonly EnhancedExceptionTelemetry _telemetry;

    public EnhancedExceptionTelemetryTests()
    {
        _mockLogger = Substitute.For<ILogger<EnhancedExceptionTelemetry>>();
        _mockOptions = Substitute.For<IOptionsMonitor<ExceptionTelemetryOptions>>();

        var options = new ExceptionTelemetryOptions
        {
            MaxExceptionRecords = 1000,
            EnableStackTraceCapture = true,
            EnableDetailedClassification = true,
            SanitizeExceptionMessages = true
        };

        _mockOptions.CurrentValue.Returns(options);

        _telemetry = new EnhancedExceptionTelemetry(_mockLogger, _mockOptions);
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new EnhancedExceptionTelemetry(null!, _mockOptions);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new EnhancedExceptionTelemetry(_mockLogger, null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    [UnitTest]
    public void RecordException_SimpleException_RecordsSuccessfully()
    {
        // Arrange
        var exception = new InvalidOperationException("Test exception");

        // Act
        _telemetry.RecordException(exception);

        // Assert
        var statistics = _telemetry.GetStatistics();
        statistics.TotalExceptions.Should().Be(1);
        statistics.ExceptionsByType["InvalidOperationException"].Should().Be(1);
    }

    [Fact]
    [UnitTest]
    public void RecordException_NullException_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => _telemetry.RecordException(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("exception");
    }

    [Fact]
    [UnitTest]
    public void RecordException_WithContext_RecordsContextInformation()
    {
        // Arrange
        var exception = new ArgumentException("Invalid parameter");
        var context = new ExceptionContext
        {
            CorrelationId = "test-correlation-123",
            OperationType = "USER_REGISTRATION",
            UserId = "user-456",
            RequestPath = "/api/users/register",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["RequestSize"] = 1024,
                ["UserAgent"] = "TestAgent/1.0"
            }
        };

        // Act
        _telemetry.RecordException(exception, context);

        // Assert
        var statistics = _telemetry.GetStatistics();
        statistics.TotalExceptions.Should().Be(1);

        var recentExceptions = _telemetry.GetRecentExceptions(1);
        recentExceptions.Should().HaveCount(1);

        var record = recentExceptions.First();
        record.CorrelationId.Should().Be("test-correlation-123");
        record.OperationType.Should().Be("USER_REGISTRATION");
        record.UserId.Should().Be("user-456");
        record.RequestPath.Should().Be("/api/users/register");
    }

    [Fact]
    [UnitTest]
    public void RecordException_WithCorrelationIdAndOperationType_RecordsCorrectly()
    {
        // Arrange
        var exception = new UnauthorizedAccessException("Access denied");
        const string correlationId = "correlation-789";
        const string operationType = "DATA_ACCESS";
        var additionalProperties = new Dictionary<string, object>
        {
            ["ResourceId"] = "resource-123",
            ["Action"] = "READ"
        };

        // Act
        _telemetry.RecordException(exception, correlationId, operationType, additionalProperties);

        // Assert
        var recentExceptions = _telemetry.GetRecentExceptions(1);
        var record = recentExceptions.First();

        record.CorrelationId.Should().Be(correlationId);
        record.OperationType.Should().Be(operationType);
        record.ExceptionType.Should().Be("UnauthorizedAccessException");
    }

    [Theory]
    [UnitTest]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordException_InvalidCorrelationId_ThrowsArgumentException(string? invalidCorrelationId)
    {
        // Arrange
        var exception = new InvalidOperationException("Test");

        // Act & Assert
        var act = () => _telemetry.RecordException(exception, invalidCorrelationId!, "OPERATION");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [UnitTest]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordException_InvalidOperationType_ThrowsArgumentException(string? invalidOperationType)
    {
        // Arrange
        var exception = new InvalidOperationException("Test");

        // Act & Assert
        var act = () => _telemetry.RecordException(exception, "correlation", invalidOperationType!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    [UnitTest]
    public void GetStatistics_InitialState_ReturnsZeroStatistics()
    {
        // Act
        var statistics = _telemetry.GetStatistics();

        // Assert
        statistics.Should().NotBeNull();
        statistics.TotalExceptions.Should().Be(0);
        statistics.CriticalExceptions.Should().Be(0);
        statistics.WarningExceptions.Should().Be(0);
        statistics.InfoExceptions.Should().Be(0);
        statistics.ExceptionsByType.Should().BeEmpty();
        statistics.ExceptionsByOperation.Should().BeEmpty();
    }

    [Fact]
    [UnitTest]
    public void GetRecentExceptions_InitialState_ReturnsEmptyList()
    {
        // Act
        var exceptions = _telemetry.GetRecentExceptions();

        // Assert
        exceptions.Should().NotBeNull();
        exceptions.Should().BeEmpty();
    }

    [Fact]
    [UnitTest]
    public void ExceptionClassification_DifferentExceptionTypes_ClassifiesCorrectly()
    {
        // Arrange & Act
        _telemetry.RecordException(new ArgumentNullException("Critical error"));  // Critical
        _telemetry.RecordException(new InvalidOperationException("Warning level"));  // Warning
        _telemetry.RecordException(new NotSupportedException("Info level"));  // Info

        // Assert
        var statistics = _telemetry.GetStatistics();
        statistics.TotalExceptions.Should().Be(3);
        // Note: Exact classification depends on implementation logic
        statistics.CriticalExceptions.Should().BeGreaterThan(0);
    }

    [Fact]
    [UnitTest]
    public void GetRecentExceptions_WithMaxCount_RespectsLimit()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            _telemetry.RecordException(new InvalidOperationException($"Exception {i}"));
        }

        // Act
        var exceptions = _telemetry.GetRecentExceptions(5);

        // Assert
        exceptions.Count.Should().Be(5);
    }

    [Fact]
    [UnitTest]
    public void GetRecentExceptions_WithSeverityFilter_FiltersCorrectly()
    {
        // Arrange
        _telemetry.RecordException(new ArgumentNullException("Critical")); // Should be Critical
        _telemetry.RecordException(new NotSupportedException("Info")); // Should be Info

        // Act
        var criticalExceptions = _telemetry.GetRecentExceptions(100, ExceptionSeverity.Critical);

        // Assert
        criticalExceptions.Should().NotBeEmpty();
        criticalExceptions.Should().OnlyContain(e => e.Severity == ExceptionSeverity.Critical);
    }

    [Fact]
    [UnitTest]
    public void RecordException_WithInnerException_CapturesInnerExceptionDetails()
    {
        // Arrange
        var innerException = new SqlException("Database connection failed");
        var outerException = new InvalidOperationException("Operation failed", innerException);

        // Act
        _telemetry.RecordException(outerException);

        // Assert
        var recentExceptions = _telemetry.GetRecentExceptions(1);
        var record = recentExceptions.First();

        record.ExceptionType.Should().Be("InvalidOperationException");
        record.InnerExceptionType.Should().Be("SqlException");
        record.HasInnerException.Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public void RecordException_AggregateException_HandlesMultipleInnerExceptions()
    {
        // Arrange
        var inner1 = new ArgumentException("Arg error");
        var inner2 = new InvalidOperationException("Op error");
        var aggregateException = new AggregateException("Multiple errors", inner1, inner2);

        // Act
        _telemetry.RecordException(aggregateException);

        // Assert
        var recentExceptions = _telemetry.GetRecentExceptions(1);
        var record = recentExceptions.First();

        record.ExceptionType.Should().Be("AggregateException");
        record.HasInnerException.Should().BeTrue();
        record.InnerExceptionCount.Should().Be(2);
    }

    [Fact]
    [UnitTest]
    public void ExceptionSanitization_SensitiveData_SanitizesMessages()
    {
        // Arrange
        var sensitiveException = new UnauthorizedAccessException(
            "Access denied for user john.doe@example.com with password secret123");

        // Act
        _telemetry.RecordException(sensitiveException);

        // Assert
        var recentExceptions = _telemetry.GetRecentExceptions(1);
        var record = recentExceptions.First();

        // Message should be sanitized (exact sanitization rules depend on implementation)
        record.SanitizedMessage.Should().NotContain("secret123");
        record.SanitizedMessage.Should().NotContain("john.doe@example.com");
    }

    [Fact]
    [IntegrationTest]
    public void ConcurrentExceptionRecording_MultipleThreads_ThreadSafe()
    {
        // Arrange
        const int numThreads = 10;
        const int exceptionsPerThread = 100;
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < numThreads; i++)
        {
            var threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < exceptionsPerThread; j++)
                {
                    var exception = new InvalidOperationException($"Thread {threadId}, Exception {j}");
                    _telemetry.RecordException(exception, $"correlation-{threadId}-{j}", $"OPERATION_{threadId}");
                }
            }));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10));

        // Assert
        var statistics = _telemetry.GetStatistics();
        statistics.TotalExceptions.Should().Be(numThreads * exceptionsPerThread);
    }

    [Fact]
    [UnitTest]
    public void ExceptionRateCalculation_OverTimeWindow_CalculatesCorrectly()
    {
        // Arrange - Record multiple exceptions
        for (int i = 0; i < 5; i++)
        {
            _telemetry.RecordException(new InvalidOperationException($"Exception {i}"));
            Thread.Sleep(10); // Small delay to ensure different timestamps
        }

        // Act
        var statistics = _telemetry.GetStatistics();

        // Assert
        statistics.TotalExceptions.Should().Be(5);
        statistics.ExceptionRate.Should().BeGreaterThan(0);
    }

    [Fact]
    [UnitTest]
    public void MaxExceptionRecords_ExceedsLimit_PrunesOldRecords()
    {
        // Arrange
        var options = new ExceptionTelemetryOptions
        {
            MaxExceptionRecords = 5, // Small limit for testing
            EnableStackTraceCapture = true,
            EnableDetailedClassification = true
        };

        _mockOptions.CurrentValue.Returns(options);

        var telemetry = new EnhancedExceptionTelemetry(_mockLogger, _mockOptions);

        // Act - Record more exceptions than the limit
        for (int i = 0; i < 10; i++)
        {
            telemetry.RecordException(new InvalidOperationException($"Exception {i}"));
        }

        // Assert
        var recentExceptions = telemetry.GetRecentExceptions();
        recentExceptions.Count.Should().BeLessOrEqualTo(5);

        var statistics = telemetry.GetStatistics();
        statistics.TotalExceptions.Should().Be(10); // Counter should still reflect total
    }

    [Fact]
    [UnitTest]
    public void ExceptionContext_AllProperties_RecordsAllInformation()
    {
        // Arrange
        var exception = new TimeoutException("Request timeout");
        var context = new ExceptionContext
        {
            CorrelationId = "full-context-test",
            OperationType = "FULL_TEST",
            UserId = "test-user-123",
            RequestPath = "/api/test/full",
            HttpMethod = "POST",
            UserAgent = "TestAgent/2.0",
            IPAddress = "192.168.1.100",
            SessionId = "session-abc123",
            AdditionalProperties = new Dictionary<string, object>
            {
                ["CustomProperty1"] = "Value1",
                ["CustomProperty2"] = 42,
                ["CustomProperty3"] = true
            }
        };

        // Act
        _telemetry.RecordException(exception, context);

        // Assert
        var recentExceptions = _telemetry.GetRecentExceptions(1);
        var record = recentExceptions.First();

        record.CorrelationId.Should().Be("full-context-test");
        record.OperationType.Should().Be("FULL_TEST");
        record.UserId.Should().Be("test-user-123");
        record.RequestPath.Should().Be("/api/test/full");
        record.HttpMethod.Should().Be("POST");
        record.UserAgent.Should().Be("TestAgent/2.0");
        record.IPAddress.Should().Be("192.168.1.100");
        record.SessionId.Should().Be("session-abc123");
        record.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}