// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Npgsql;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.Server.Features.Infrastructure.Resilience;

/// <summary>
/// Extensions for adding database resilience patterns including connection pool circuit breakers.
/// </summary>
internal static class DatabaseResilienceExtensions
{
    /// <summary>
    /// Adds database resilience patterns to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration for circuit breaker options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDatabaseResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure database-specific circuit breaker options
        services.Configure<DatabaseCircuitBreakerOptions>(
            configuration.GetSection(DatabaseCircuitBreakerOptions.SectionName));

        // Register database circuit breaker factory
        services.AddSingleton<DatabaseCircuitBreakerFactory>();

        return services;
    }

    /// <summary>
    /// Creates a resilience policy for database operations.
    /// </summary>
    /// <param name="serviceProvider">Service provider.</param>
    /// <returns>Database resilience policy.</returns>
    public static IAsyncPolicy CreateDatabasePolicy(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<DatabaseCircuitBreakerOptions>>().Value;
        var logger = serviceProvider.GetRequiredService<ILogger<DatabaseResilienceExtensions>>();
        var connectionPoolMetrics = serviceProvider.GetRequiredService<ConnectionPoolMetrics>();

        // Create retry policy for transient database errors
        var retryPolicy = Policy
            .Handle<NpgsqlException>(ex => IsTransientError(ex))
            .Or<TimeoutException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: options.MaxRetryAttempts,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                    Math.Min(
                        options.InitialRetryDelayMs * Math.Pow(2, retryAttempt - 1),
                        options.MaxRetryDelayMs)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    logger.LogWarning(
                        "Database retry {RetryCount} after {Delay}ms. Reason: {Reason}",
                        retryCount,
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? "Unknown");
                });

        // Create circuit breaker for database connectivity
        var circuitBreakerPolicy = Policy
            .Handle<NpgsqlException>(ex => IsCircuitBreakerTriggerError(ex))
            .Or<TimeoutException>()
            .Or<TaskCanceledException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.FailureThreshold,
                durationOfBreak: options.DurationOfBreak,
                onBreak: (exception, duration) =>
                {
                    logger.LogError(
                        "Database circuit breaker opened for {Duration}ms. Reason: {Reason}",
                        duration.TotalMilliseconds,
                        exception.Message);

                    connectionPoolMetrics.RecordConnectionAcquisitionFailure("CircuitBreakerOpen");
                },
                onReset: () =>
                {
                    logger.LogInformation("Database circuit breaker reset");
                },
                onHalfOpen: () =>
                {
                    logger.LogInformation("Database circuit breaker half-open");
                });

        // Create timeout policy
        var timeoutPolicy = Policy.TimeoutAsync(options.Timeout);

        // Combine policies: Timeout -> Retry -> Circuit Breaker
        return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy, timeoutPolicy);
    }

    /// <summary>
    /// Determines if an NpgsqlException represents a transient error that should trigger retry.
    /// </summary>
    /// <param name="ex">The exception to evaluate.</param>
    /// <returns>True if the error is transient.</returns>
    private static bool IsTransientError(NpgsqlException ex)
    {
        // PostgreSQL error codes that are typically transient
        return ex.SqlState switch
        {
            // Connection failure
            "08000" => true, // connection_exception
            "08003" => true, // connection_does_not_exist
            "08006" => true, // connection_failure
            "08001" => true, // sqlclient_unable_to_establish_sqlconnection
            "08004" => true, // sqlserver_rejected_establishment_of_sqlconnection

            // System resource errors
            "53000" => true, // insufficient_resources
            "53100" => true, // disk_full
            "53200" => true, // out_of_memory
            "53300" => true, // too_many_connections

            // Lock timeout
            "55P03" => true, // lock_not_available

            // Admin shutdown
            "57P01" => true, // admin_shutdown
            "57P02" => true, // crash_shutdown
            "57P03" => true, // cannot_connect_now

            // Default: not transient
            _ => false
        };
    }

    /// <summary>
    /// Determines if an error should trigger the circuit breaker.
    /// </summary>
    /// <param name="ex">The exception to evaluate.</param>
    /// <returns>True if the error should trigger circuit breaker.</returns>
    private static bool IsCircuitBreakerTriggerError(NpgsqlException ex)
    {
        // Trigger circuit breaker for connection failures and resource exhaustion
        return ex.SqlState switch
        {
            // Connection failures
            "08000" => true, // connection_exception
            "08003" => true, // connection_does_not_exist
            "08006" => true, // connection_failure

            // Resource exhaustion
            "53000" => true, // insufficient_resources
            "53200" => true, // out_of_memory
            "53300" => true, // too_many_connections

            // Database unavailable
            "57P01" => true, // admin_shutdown
            "57P02" => true, // crash_shutdown
            "57P03" => true, // cannot_connect_now

            _ => false
        };
    }
}

/// <summary>
/// Factory for creating database circuit breakers.
/// </summary>
internal sealed class DatabaseCircuitBreakerFactory
{
    private readonly DatabaseCircuitBreakerOptions _options;
    private readonly ILogger<DatabaseCircuitBreakerFactory> _logger;
    private readonly ConnectionPoolMetrics _connectionPoolMetrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCircuitBreakerFactory"/> class.
    /// </summary>
    /// <param name="options">Database circuit breaker options.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="connectionPoolMetrics">Connection pool metrics.</param>
    public DatabaseCircuitBreakerFactory(
        IOptions<DatabaseCircuitBreakerOptions> options,
        ILogger<DatabaseCircuitBreakerFactory> logger,
        ConnectionPoolMetrics connectionPoolMetrics)
    {
        _options = options.Value;
        _logger = logger;
        _connectionPoolMetrics = connectionPoolMetrics;
    }

    /// <summary>
    /// Creates a circuit breaker for database connections.
    /// </summary>
    /// <returns>Database connection circuit breaker policy.</returns>
    public IAsyncPolicy CreateConnectionCircuitBreaker()
    {
        return Policy
            .Handle<NpgsqlException>(ex => IsCircuitBreakerTriggerError(ex))
            .Or<TimeoutException>()
            .Or<TaskCanceledException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: _options.FailureThreshold,
                durationOfBreak: _options.DurationOfBreak,
                onBreak: (exception, duration) =>
                {
                    _logger.LogError(
                        "Database connection circuit breaker opened for {Duration}ms. Reason: {Reason}",
                        duration.TotalMilliseconds,
                        exception.Message);

                    _connectionPoolMetrics.RecordConnectionAcquisitionFailure("CircuitBreakerOpen");
                },
                onReset: () =>
                {
                    _logger.LogInformation("Database connection circuit breaker reset");
                },
                onHalfOpen: () =>
                {
                    _logger.LogInformation("Database connection circuit breaker half-open");
                });
    }

    /// <summary>
    /// Creates a circuit breaker for database queries.
    /// </summary>
    /// <returns>Database query circuit breaker policy.</returns>
    public IAsyncPolicy CreateQueryCircuitBreaker()
    {
        return Policy
            .Handle<NpgsqlException>(ex => IsTransientError(ex))
            .Or<TimeoutException>()
            .Or<TaskCanceledException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: _options.FailureThreshold * 2, // Allow more failures for queries
                durationOfBreak: TimeSpan.FromSeconds(_options.DurationOfBreakSeconds / 2), // Shorter break for queries
                onBreak: (exception, duration) =>
                {
                    _logger.LogWarning(
                        "Database query circuit breaker opened for {Duration}ms. Reason: {Reason}",
                        duration.TotalMilliseconds,
                        exception.Message);
                },
                onReset: () =>
                {
                    _logger.LogInformation("Database query circuit breaker reset");
                },
                onHalfOpen: () =>
                {
                    _logger.LogInformation("Database query circuit breaker half-open");
                });
    }
}

/// <summary>
/// Database-specific circuit breaker configuration options.
/// </summary>
public sealed class DatabaseCircuitBreakerOptions
{
    /// <summary>
    /// Configuration section name for database circuit breaker settings.
    /// </summary>
    public const string SectionName = "Database:CircuitBreaker";

    /// <summary>
    /// Number of consecutive failures before opening the circuit.
    /// Default is 5 failures.
    /// </summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>
    /// Duration the circuit stays open before attempting to close.
    /// Default is 30 seconds.
    /// </summary>
    public int DurationOfBreakSeconds { get; init; } = 30;

    /// <summary>
    /// Timeout for individual operations in seconds.
    /// Default is 30 seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Maximum number of retry attempts.
    /// Default is 3 retries.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Initial retry delay in milliseconds.
    /// Default is 500ms.
    /// </summary>
    public int InitialRetryDelayMs { get; init; } = 500;

    /// <summary>
    /// Maximum retry delay in milliseconds with exponential backoff.
    /// Default is 5000ms (5 seconds).
    /// </summary>
    public int MaxRetryDelayMs { get; init; } = 5000;

    /// <summary>
    /// Gets the duration of break as a TimeSpan.
    /// </summary>
    public TimeSpan DurationOfBreak => TimeSpan.FromSeconds(DurationOfBreakSeconds);

    /// <summary>
    /// Gets the timeout as a TimeSpan.
    /// </summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>
    /// Whether to enable database connection pooling circuit breakers.
    /// Default is true.
    /// </summary>
    public bool EnableConnectionPoolCircuitBreaker { get; init; } = true;

    /// <summary>
    /// Whether to enable database query circuit breakers.
    /// Default is true.
    /// </summary>
    public bool EnableQueryCircuitBreaker { get; init; } = true;

    /// <summary>
    /// Connection pool utilization threshold that triggers alerts.
    /// Default is 0.8 (80%).
    /// </summary>
    public double PoolUtilizationThreshold { get; init; } = 0.8;

    /// <summary>
    /// Connection acquisition timeout in seconds.
    /// Default is 5 seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Gets the connection timeout as a TimeSpan.
    /// </summary>
    public TimeSpan ConnectionTimeout => TimeSpan.FromSeconds(ConnectionTimeoutSeconds);
}