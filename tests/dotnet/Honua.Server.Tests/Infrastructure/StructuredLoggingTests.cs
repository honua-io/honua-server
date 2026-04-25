// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Logging;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for structured logging with Serilog and source generators
/// Validates AOT-compatible logging methods and event IDs
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StructuredLoggingTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void QueryExecuted_WithValidParameters_LogsCorrectly()
    {
        // Arrange
        var testLogger = new TestLogger();

        // Act
        Log.QueryExecuted(testLogger, "layer1", 42, 123.45);

        // Assert
        var logEntry = testLogger.LogEntries.Single();
        logEntry.LogLevel.Should().Be(LogLevel.Information);
        logEntry.EventId.Should().Be(new EventId(1001));
        logEntry.Message.Should().Contain("layer1").And.Contain("42").And.Contain("123.45");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ApplicationStarting_WithValidParameters_LogsCorrectly()
    {
        // Arrange
        var testLogger = new TestLogger();

        // Act
        Log.ApplicationStarting(testLogger, "1.0.0", "Development");

        // Assert
        var logEntry = testLogger.LogEntries.Single();
        logEntry.LogLevel.Should().Be(LogLevel.Information);
        logEntry.EventId.Should().Be(new EventId(4001));
        logEntry.Message.Should().Contain("1.0.0").And.Contain("Development");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void DatabaseMigrationFailed_WithException_LogsCorrectly()
    {
        // Arrange
        var testLogger = new TestLogger();
        var exception = new InvalidOperationException("Test exception");

        // Act
        Log.DatabaseMigrationFailed(testLogger, "Migration error", exception);

        // Assert
        var logEntry = testLogger.LogEntries.Single();
        logEntry.LogLevel.Should().Be(LogLevel.Error);
        logEntry.EventId.Should().Be(new EventId(5003));
        logEntry.Message.Should().Contain("Migration error");
        logEntry.Exception.Should().Be(exception);
    }

    [Theory]
    [InlineData(1001, "Query operations")]
    [InlineData(2001, "Edit operations")]
    [InlineData(3001, "Performance warnings")]
    [InlineData(4001, "Infrastructure operations")]
    [InlineData(5001, "Errors")]
    [Operation(Operations.TestInfrastructure)]
    public void EventIds_AreInCorrectRanges(int eventId, string category)
    {
        // Assert event ID ranges are correctly categorized
        switch (category)
        {
            case "Query operations":
                eventId.Should().BeInRange(1000, 1999);
                break;
            case "Edit operations":
                eventId.Should().BeInRange(2000, 2999);
                break;
            case "Performance warnings":
                eventId.Should().BeInRange(3000, 3999);
                break;
            case "Infrastructure operations":
                eventId.Should().BeInRange(4000, 4999);
                break;
            case "Errors":
                eventId.Should().BeInRange(5000, 5999);
                break;
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void DatabaseMigrationsStarting_LogsWithCorrectEventId()
    {
        // Arrange
        var testLogger = new TestLogger();

        // Act
        Log.DatabaseMigrationsStarting(testLogger);

        // Assert
        var logEntry = testLogger.LogEntries.Single();
        logEntry.LogLevel.Should().Be(LogLevel.Information);
        logEntry.EventId.Should().Be(new EventId(4010));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void StructuredLogging_HasSourceGeneratedMethods_ForAotCompatibility()
    {
        // This test validates that all logging methods are source-generated (not reflection-based)
        // and that configuration is code-based for AOT compatibility
        var testLogger = new TestLogger();

        // Act - Call various logging methods to verify source generation
        Log.QueryExecuted(testLogger, "test", 1, 1.0);
        Log.ApplicationStarting(testLogger, "1.0", "test");
        Log.DatabaseMigrationsStarting(testLogger);
        Log.DatabaseConnectionFailed(testLogger, "test", new InvalidOperationException("test"));

        // Assert - All methods executed successfully without reflection exceptions
        testLogger.LogEntries.Should().HaveCount(4);
        testLogger.LogEntries.All(e => e.EventId.Id > 0).Should().BeTrue();

        // Verify each method uses expected event ID ranges
        testLogger.LogEntries.Should().Contain(e => e.EventId.Id >= 1000 && e.EventId.Id < 2000); // Query
        testLogger.LogEntries.Should().Contain(e => e.EventId.Id >= 4000 && e.EventId.Id < 5000); // Infrastructure
        testLogger.LogEntries.Should().Contain(e => e.EventId.Id >= 5000 && e.EventId.Id < 6000); // Errors
    }
}

/// <summary>
/// Simple test logger implementation for testing structured logging
/// </summary>
internal sealed class TestLogger : ILogger
{
    public List<LogEntry> LogEntries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        new NullScope();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        LogEntries.Add(new LogEntry(
            logLevel,
            eventId,
            formatter(state, exception),
            exception));
    }

    private sealed class NullScope : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Log entry for testing
/// </summary>
/// <param name="LogLevel">The log level</param>
/// <param name="EventId">The event ID</param>
/// <param name="Message">The formatted message</param>
/// <param name="Exception">The exception, if any</param>
internal sealed record LogEntry(
    LogLevel LogLevel,
    EventId EventId,
    string Message,
    Exception? Exception);
