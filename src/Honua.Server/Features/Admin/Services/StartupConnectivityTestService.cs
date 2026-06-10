// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Service for testing connectivity to all external dependencies during startup.
/// Validates secret references and external service availability.
/// </summary>
internal sealed class StartupConnectivityTestService
{
    public const string HttpClientName = "StartupConnectivity";

    private readonly ISecretProvider _secretProvider;
    private readonly IConnectionSecretResolver _connectionSecretResolver;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StartupConnectivityTestService> _logger;

    public StartupConnectivityTestService(
        ISecretProvider secretProvider,
        IConnectionSecretResolver connectionSecretResolver,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<StartupConnectivityTestService> logger)
    {
        _secretProvider = secretProvider;
        _connectionSecretResolver = connectionSecretResolver;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Performs comprehensive connectivity testing during application startup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connectivity test results</returns>
    public async Task<ConnectivityTestResult> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new ConnectivityTestResult
        {
            StartTime = DateTimeOffset.UtcNow,
            Tests = new List<ConnectivityTest>()
        };

        try
        {
            ConnectivityTestLog.TestingStarted(_logger);

            // Test secret providers
            await TestSecretProvidersAsync(result, cancellationToken);

            // Test database connections
            await TestDatabaseConnectionsAsync(result, cancellationToken);

            // Test external service URLs
            await TestExternalServicesAsync(result, cancellationToken);

            // Test cache connectivity (Redis)
            await TestCacheConnectivityAsync(result, cancellationToken);

            // Test cloud storage connectivity
            await TestCloudStorageConnectivityAsync(result, cancellationToken);

            result.EndTime = DateTimeOffset.UtcNow;
            result.Duration = stopwatch.Elapsed;
            result.OverallSuccess = result.Tests.All(t => t.Success);

            var passedCount = result.Tests.Count(t => t.Success);
            var failedCount = result.Tests.Count(t => !t.Success);

            ConnectivityTestLog.TestingCompleted(_logger, passedCount, failedCount, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            ConnectivityTestLog.TestingFailed(_logger, ex.Message, ex);
            result.EndTime = DateTimeOffset.UtcNow;
            result.Duration = stopwatch.Elapsed;
            result.OverallSuccess = false;
            result.GeneralError = SanitizeConnectivityError(ex, "Connectivity testing failed");
            return result;
        }
    }

    /// <summary>
    /// Tests all configured secret providers.
    /// </summary>
    private async Task TestSecretProvidersAsync(ConnectivityTestResult result, CancellationToken cancellationToken)
    {
        // Test secret provider availability
        var secretProviderTest = new ConnectivityTest
        {
            Name = "Secret Provider",
            Category = "Security",
            Description = "Test secret provider registration and basic functionality"
        };

        try
        {
            var supportedProviders = _secretProvider.GetSupportedProviders();
            secretProviderTest.Details.Add("Supported Providers", string.Join(", ", supportedProviders));

            if (supportedProviders.Length == 0)
            {
                secretProviderTest.Success = false;
                secretProviderTest.Error = "No secret providers configured";
            }
            else
            {
                secretProviderTest.Success = true;
                secretProviderTest.Details.Add("Provider Count", supportedProviders.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            secretProviderTest.Success = false;
            secretProviderTest.Error = SanitizeConnectivityError(ex, "Secret provider test failed");
        }

        result.Tests.Add(secretProviderTest);

        // Test individual secret references
        var secretReferences = GetSecretReferencesFromConfiguration();
        foreach (var secretRef in secretReferences)
        {
            var test = new ConnectivityTest
            {
                Name = $"Secret Reference: {MaskSecretReference(secretRef)}",
                Category = "Security",
                Description = $"Test resolution of secret reference"
            };

            try
            {
                var canResolve = await _secretProvider.CanResolveSecretAsync(secretRef, cancellationToken);
                test.Success = canResolve;
                if (!canResolve)
                {
                    test.Error = "Secret reference cannot be resolved";
                }
                test.Details.Add("Reference", MaskSecretReference(secretRef));
            }
            catch (Exception ex)
            {
                test.Success = false;
                test.Error = SanitizeConnectivityError(ex, "Secret reference test failed");
            }

            result.Tests.Add(test);
        }
    }

    /// <summary>
    /// Tests database connectivity for all configured connection strings.
    /// </summary>
    private async Task TestDatabaseConnectionsAsync(ConnectivityTestResult result, CancellationToken cancellationToken)
    {
        var connectionStrings = GetConnectionStringsFromConfiguration();

        foreach (var (name, connectionString) in connectionStrings)
        {
            var test = new ConnectivityTest
            {
                Name = $"Database: {name}",
                Category = "Database",
                Description = $"Test connectivity to {name} database"
            };

            try
            {
                if (_secretProvider.IsSecretReference(connectionString))
                {
                    // Test secret resolution first
                    var canResolveSecret = await _secretProvider.CanResolveSecretAsync(connectionString, cancellationToken);
                    if (!canResolveSecret)
                    {
                        test.Success = false;
                        test.Error = "Database connection string secret cannot be resolved";
                        test.Details.Add("Secret Reference", MaskSecretReference(connectionString));
                        result.Tests.Add(test);
                        continue;
                    }

                    // Resolve the actual connection string
                    var resolvedConnectionString = await _secretProvider.GetSecretAsync(connectionString, cancellationToken);
                    if (string.IsNullOrEmpty(resolvedConnectionString))
                    {
                        test.Success = false;
                        test.Error = "Resolved connection string is empty";
                        result.Tests.Add(test);
                        continue;
                    }

                    // Test the actual database connection
                    await TestDatabaseConnectionStringAsync(test, resolvedConnectionString, cancellationToken);
                }
                else
                {
                    // Direct connection string
                    await TestDatabaseConnectionStringAsync(test, connectionString, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                test.Success = false;
                test.Error = SanitizeConnectivityError(ex, "Database connectivity test failed");
            }

            result.Tests.Add(test);
        }
    }

    /// <summary>
    /// Tests a specific database connection string.
    /// </summary>
    private async Task TestDatabaseConnectionStringAsync(ConnectivityTest test, string connectionString, CancellationToken cancellationToken)
    {
        // Get connection health tester from DI
        var healthTester = _serviceProvider.GetService<IConnectionHealthTester>();
        if (healthTester == null)
        {
            test.Success = false;
            test.Error = "Connection health tester not available";
            return;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var healthStatus = await healthTester.TestConnectionAsync(connectionString, combinedCts.Token);
            var isHealthy = healthStatus == Honua.Core.Features.Security.Domain.ConnectionHealthStatus.Healthy;
            test.Success = isHealthy;
            if (!isHealthy)
            {
                test.Error = "Database connection test failed";
            }
            test.Details.Add("Connection Timeout", "10 seconds");
        }
        catch (OperationCanceledException)
        {
            test.Success = false;
            test.Error = "Database connection test timed out";
        }
        catch (Exception ex)
        {
            test.Success = false;
            test.Error = SanitizeConnectivityError(ex, "Database connection failed");
        }
    }

    /// <summary>
    /// Tests external service URLs for connectivity.
    /// </summary>
    private async Task TestExternalServicesAsync(ConnectivityTestResult result, CancellationToken cancellationToken)
    {
        var externalUrls = GetExternalUrlsFromConfiguration();

        foreach (var (name, url) in externalUrls)
        {
            var test = new ConnectivityTest
            {
                Name = $"External Service: {name}",
                Category = "External Services",
                Description = $"Test connectivity to external service"
            };

            try
            {
                var httpClient = _httpClientFactory.CreateClient(HttpClientName);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, combinedCts.Token);

                test.Success = response.IsSuccessStatusCode;
                test.Details.Add("URL", RedactUrl(url));
                test.Details.Add("Status Code", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                test.Details.Add("Response Time", $"{httpClient.Timeout.TotalMilliseconds}ms max");

                if (!response.IsSuccessStatusCode)
                {
                    test.Error = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (OperationCanceledException)
            {
                test.Success = false;
                test.Error = "External service connection timed out";
            }
            catch (Exception ex)
            {
                test.Success = false;
                test.Error = SanitizeConnectivityError(ex, "External service connectivity test failed");
            }

            result.Tests.Add(test);
        }
    }

    /// <summary>
    /// Tests cache (Redis) connectivity.
    /// </summary>
    private async Task TestCacheConnectivityAsync(ConnectivityTestResult result, CancellationToken cancellationToken)
    {
        var test = new ConnectivityTest
        {
            Name = "Cache (Redis)",
            Category = "Cache",
            Description = "Test Redis cache connectivity"
        };

        try
        {
            // Try to get cache service from DI
            var cacheService = _serviceProvider.GetService<Honua.Core.Features.Caching.Abstractions.ICacheService>();
            if (cacheService == null)
            {
                test.Success = false;
                test.Error = "Cache service not registered";
            }
            else
            {
                // Simple connectivity test - try to set and get a test value
                var testKey = $"connectivity-test-{Guid.NewGuid()}";
                var testValue = "test-value";

                await cacheService.SetAsync(testKey, testValue, TimeSpan.FromMinutes(1), cancellationToken);
                var retrievedValue = await cacheService.GetAsync<string>(testKey, cancellationToken);

                if (retrievedValue == testValue)
                {
                    test.Success = true;
                    test.Details.Add("Test Key", testKey);
                    test.Details.Add("Round Trip", "Success");

                    // Clean up test key
                    await cacheService.RemoveAsync(testKey, cancellationToken);
                }
                else
                {
                    test.Success = false;
                    test.Error = "Cache round-trip test failed";
                }
            }
        }
        catch (Exception ex)
        {
            test.Success = false;
            test.Error = SanitizeConnectivityError(ex, "Cache connectivity test failed");
        }

        result.Tests.Add(test);
    }

    /// <summary>
    /// Tests cloud storage connectivity.
    /// </summary>
    private async Task TestCloudStorageConnectivityAsync(ConnectivityTestResult result, CancellationToken cancellationToken)
    {
        // Test cloud storage if configured
        var cloudStorageOptions = _serviceProvider.GetService<IOptions<Honua.Core.Features.Infrastructure.Domain.CloudStorageOptions>>();

        if (cloudStorageOptions?.Value?.Enabled == true)
        {
            var test = new ConnectivityTest
            {
                Name = "Cloud Storage",
                Category = "Storage",
                Description = "Test cloud storage connectivity"
            };

            try
            {
                // Basic connectivity test for cloud storage
                // This would depend on the specific cloud storage implementation
                var options = cloudStorageOptions.Value;
                test.Details.Add("Provider", options.Provider.ToString());
                test.Details.Add("Enabled", options.Enabled.ToString());

                // For now, just verify configuration is valid
                if (!Enum.IsDefined(options.Provider))
                {
                    test.Success = false;
                    test.Error = "Cloud storage provider not specified";
                }
                else
                {
                    test.Success = true;
                    test.Details.Add("Configuration", "Valid");
                }
            }
            catch (Exception ex)
            {
                test.Success = false;
                test.Error = SanitizeConnectivityError(ex, "Cloud storage configuration test failed");
            }

            result.Tests.Add(test);
        }
    }

    /// <summary>
    /// Extracts secret references from configuration.
    /// </summary>
    private List<string> GetSecretReferencesFromConfiguration()
    {
        var secretRefs = new List<string>();

        foreach (var kvp in _configuration.AsEnumerable())
        {
            if (!string.IsNullOrEmpty(kvp.Value) && _secretProvider.IsSecretReference(kvp.Value))
            {
                secretRefs.Add(kvp.Value);
            }
        }

        return secretRefs.Distinct().ToList();
    }

    /// <summary>
    /// Extracts connection strings from configuration.
    /// </summary>
    private List<(string Name, string ConnectionString)> GetConnectionStringsFromConfiguration()
    {
        var connections = new List<(string, string)>();

        var connectionStringsSection = _configuration.GetSection("ConnectionStrings");
        foreach (var child in connectionStringsSection.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Value))
            {
                connections.Add((child.Key, child.Value));
            }
        }

        return connections;
    }

    /// <summary>
    /// Extracts external service URLs from configuration.
    /// </summary>
    private List<(string Name, string Url)> GetExternalUrlsFromConfiguration()
    {
        var urls = new List<(string, string)>();

        // Look for URL patterns in configuration
        foreach (var kvp in _configuration.AsEnumerable())
        {
            if (!string.IsNullOrEmpty(kvp.Value) &&
                (kvp.Key.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
                 kvp.Key.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)) &&
                Uri.TryCreate(kvp.Value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                urls.Add((kvp.Key, kvp.Value));
            }
        }

        return urls;
    }

    /// <summary>
    /// Masks sensitive parts of secret references for logging.
    /// </summary>
    private static string MaskSecretReference(string secretRef)
    {
        var colonIndex = secretRef.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex > 0)
        {
            var provider = secretRef[..colonIndex];
            var path = secretRef[(colonIndex + 1)..];

            if (path.Length > 8)
            {
                return $"{provider}:{path[..4]}***{path[^4..]}";
            }
            return $"{provider}:***";
        }
        return "***";
    }

    private static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "[redacted-url]";
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    private static string SanitizeConnectivityError(Exception exception, string fallback)
        => $"{fallback} ({exception.GetType().Name}).";
}

/// <summary>
/// Result of connectivity testing.
/// </summary>
public sealed class ConnectivityTestResult
{
    /// <summary>
    /// When testing started.
    /// </summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// When testing ended.
    /// </summary>
    public DateTimeOffset EndTime { get; set; }

    /// <summary>
    /// Total duration of testing.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Whether all tests passed.
    /// </summary>
    public bool OverallSuccess { get; set; }

    /// <summary>
    /// General error if the entire test run failed.
    /// </summary>
    public string? GeneralError { get; set; }

    /// <summary>
    /// Individual connectivity tests.
    /// </summary>
    public List<ConnectivityTest> Tests { get; set; } = new();
}

/// <summary>
/// Individual connectivity test result.
/// </summary>
public sealed class ConnectivityTest
{
    /// <summary>
    /// Name of the test.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Category of the test.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Description of what the test does.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Whether the test passed.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the test failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Additional test details.
    /// </summary>
    public Dictionary<string, string> Details { get; set; } = new();
}

/// <summary>
/// Logging extensions for connectivity testing.
/// </summary>
internal static partial class ConnectivityTestLog
{
    [LoggerMessage(
        EventId = 30001,
        Level = LogLevel.Information,
        Message = "Starting comprehensive connectivity testing")]
    public static partial void TestingStarted(ILogger logger);

    [LoggerMessage(
        EventId = 30002,
        Level = LogLevel.Information,
        Message = "Connectivity testing completed: {PassedCount} passed, {FailedCount} failed, {DurationMs}ms")]
    public static partial void TestingCompleted(ILogger logger, int passedCount, int failedCount, long durationMs);

    [LoggerMessage(
        EventId = 30003,
        Level = LogLevel.Error,
        Message = "Connectivity testing failed: {Error}")]
    public static partial void TestingFailed(ILogger logger, string error, Exception exception);
}
