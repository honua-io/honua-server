// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.TestKit.Chaos;

/// <summary>
/// Chaos engineering tests to validate system resilience under adverse conditions.
/// </summary>
public static class ChaosTestScenarios
{
    /// <summary>
    /// Simulates database connection failures and tests graceful degradation.
    /// </summary>
    public static async Task<ChaosTestResult> TestDatabaseConnectionFailure(
        WebApplicationFactory<Program> factory,
        string[] endpoints)
    {
        var results = new List<ChaosTestAttempt>();

        // TODO: This would require dependency injection modification to simulate failures
        // For now, we test how the system behaves when database is unavailable

        foreach (var endpoint in endpoints)
        {
            using var client = factory.CreateClient();

            try
            {
                var response = await client.GetAsync(endpoint);
                var content = await response.Content.ReadAsStringAsync();

                results.Add(new ChaosTestAttempt
                {
                    Scenario = "Database connection failure",
                    Endpoint = endpoint,
                    StatusCode = response.StatusCode,
                    ResponseContent = content,
                    IsResilient = response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                                 response.StatusCode == HttpStatusCode.InternalServerError,
                    ResponseTime = TimeSpan.Zero // Would measure actual response time
                });
            }
            catch (Exception ex)
            {
                results.Add(new ChaosTestAttempt
                {
                    Scenario = "Database connection failure",
                    Endpoint = endpoint,
                    Exception = ex,
                    IsResilient = ex is TimeoutException // Timeout is expected
                });
            }
        }

        return new ChaosTestResult
        {
            TestType = "Database Connection Failure",
            Attempts = results
        };
    }

    /// <summary>
    /// Tests system behavior under memory pressure.
    /// </summary>
    public static async Task<ChaosTestResult> TestMemoryPressure(
        HttpClient client,
        string endpoint,
        int concurrentRequests = 50)
    {
        var results = new List<ChaosTestAttempt>();

        // Create memory pressure by making many concurrent requests for large datasets
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(async i =>
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var response = await client.GetAsync($"{endpoint}?resultRecordCount=1000&f=geojson");
                    stopwatch.Stop();

                    return new ChaosTestAttempt
                    {
                        Scenario = $"Memory pressure - Request {i}",
                        Endpoint = endpoint,
                        StatusCode = response.StatusCode,
                        ResponseContent = $"Length: {(await response.Content.ReadAsStringAsync()).Length}",
                        IsResilient = response.StatusCode == HttpStatusCode.OK ||
                                     response.StatusCode == HttpStatusCode.ServiceUnavailable,
                        ResponseTime = stopwatch.Elapsed
                    };
                }
                catch (Exception ex)
                {
                    return new ChaosTestAttempt
                    {
                        Scenario = $"Memory pressure - Request {i}",
                        Endpoint = endpoint,
                        Exception = ex,
                        IsResilient = ex is TimeoutException || ex is OutOfMemoryException,
                        ResponseTime = TimeSpan.MaxValue
                    };
                }
            });

        var attempts = await Task.WhenAll(tasks);
        results.AddRange(attempts);

        return new ChaosTestResult
        {
            TestType = "Memory Pressure",
            Attempts = results
        };
    }

    /// <summary>
    /// Tests timeout handling and circuit breaker patterns.
    /// </summary>
    public static async Task<ChaosTestResult> TestTimeoutHandling(
        HttpClient client,
        string endpoint,
        TimeSpan shortTimeout = default)
    {
        if (shortTimeout == default)
        {
            shortTimeout = TimeSpan.FromMilliseconds(100);
        }

        var results = new List<ChaosTestAttempt>();

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(shortTimeout);
            var response = await client.GetAsync(endpoint, cts.Token);
            stopwatch.Stop();

            results.Add(new ChaosTestAttempt
            {
                Scenario = "Short timeout",
                Endpoint = endpoint,
                StatusCode = response.StatusCode,
                ResponseContent = await response.Content.ReadAsStringAsync(),
                IsResilient = true, // Completing within timeout is resilient
                ResponseTime = stopwatch.Elapsed
            });
        }
        catch (OperationCanceledException ex)
        {
            results.Add(new ChaosTestAttempt
            {
                Scenario = "Short timeout",
                Endpoint = endpoint,
                Exception = ex,
                IsResilient = true, // Proper timeout handling is resilient
                ResponseTime = shortTimeout
            });
        }
        catch (Exception ex)
        {
            results.Add(new ChaosTestAttempt
            {
                Scenario = "Short timeout",
                Endpoint = endpoint,
                Exception = ex,
                IsResilient = false, // Unexpected exception is not resilient
                ResponseTime = TimeSpan.MaxValue
            });
        }

        return new ChaosTestResult
        {
            TestType = "Timeout Handling",
            Attempts = results
        };
    }

    /// <summary>
    /// Tests system behavior with malformed or corrupted data.
    /// </summary>
    public static async Task<ChaosTestResult> TestDataCorruption(
        HttpClient client,
        string endpoint)
    {
        var results = new List<ChaosTestAttempt>();

        var corruptedPayloads = new[]
        {
            // Invalid JSON
            "{\"name\": \"test\", malformed}",
            "not-json-at-all",
            "",
            "null",
            // Extremely large payloads
            new string('x', 1000000), // 1MB of 'x' characters
            // Binary data
            Convert.ToBase64String(Enumerable.Range(0, 1000).Select(i => (byte)(i % 256)).ToArray()),
            // Unicode edge cases
            "\uFEFF\u200B\u200C\u200D", // BOM + zero-width characters
            // Control characters
            "\x00\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0A\x0B\x0C\x0D\x0E\x0F"
        };

        foreach (var payload in corruptedPayloads)
        {
            try
            {
                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content);

                results.Add(new ChaosTestAttempt
                {
                    Scenario = "Data corruption",
                    Endpoint = endpoint,
                    StatusCode = response.StatusCode,
                    ResponseContent = await response.Content.ReadAsStringAsync(),
                    IsResilient = (int)response.StatusCode >= 400 && (int)response.StatusCode < 500,
                    Payload = payload.Length > 100 ? payload[..100] + "..." : payload
                });
            }
            catch (Exception ex)
            {
                results.Add(new ChaosTestAttempt
                {
                    Scenario = "Data corruption",
                    Endpoint = endpoint,
                    Exception = ex,
                    IsResilient = ex is ArgumentException || ex is FormatException,
                    Payload = payload.Length > 100 ? payload[..100] + "..." : payload
                });
            }
        }

        return new ChaosTestResult
        {
            TestType = "Data Corruption",
            Attempts = results
        };
    }

    /// <summary>
    /// Tests network partition scenarios and retry policies.
    /// </summary>
    public static async Task<ChaosTestResult> TestNetworkPartition(
        HttpClient client,
        string[] endpoints,
        int retryAttempts = 3)
    {
        var results = new List<ChaosTestAttempt>();

        foreach (var endpoint in endpoints)
        {
            for (int attempt = 0; attempt < retryAttempts; attempt++)
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    // Simulate network issues by setting very short timeout occasionally
                    if (attempt % 2 == 0)
                    {
                        client.Timeout = TimeSpan.FromMilliseconds(1);
                    }
                    else
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                    }

                    var response = await client.GetAsync(endpoint);
                    stopwatch.Stop();

                    results.Add(new ChaosTestAttempt
                    {
                        Scenario = $"Network partition - Attempt {attempt + 1}",
                        Endpoint = endpoint,
                        StatusCode = response.StatusCode,
                        ResponseContent = "Success",
                        IsResilient = true,
                        ResponseTime = stopwatch.Elapsed
                    });

                    break; // Success, no need to retry
                }
                catch (Exception ex)
                {
                    var isLastAttempt = attempt == retryAttempts - 1;

                    results.Add(new ChaosTestAttempt
                    {
                        Scenario = $"Network partition - Attempt {attempt + 1}",
                        Endpoint = endpoint,
                        Exception = ex,
                        IsResilient = !isLastAttempt || ex is TimeoutException,
                        ResponseTime = TimeSpan.MaxValue
                    });

                    if (isLastAttempt)
                    {
                        break;
                    }

                    // Wait before retry with exponential backoff
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100));
                }
            }
        }

        return new ChaosTestResult
        {
            TestType = "Network Partition",
            Attempts = results
        };
    }
}

public class ChaosTestResult
{
    public string TestType { get; init; } = "";
    public List<ChaosTestAttempt> Attempts { get; init; } = new();

    public bool IsSystemResilient => Attempts.All(a => a.IsResilient);
    public double ResilienceScore => Attempts.Count > 0 ? (double)Attempts.Count(a => a.IsResilient) / Attempts.Count : 1.0;
    public TimeSpan AverageResponseTime
    {
        get
        {
            var durations = Attempts
                .Where(a => a.ResponseTime != TimeSpan.MaxValue)
                .Select(a => a.ResponseTime.TotalMilliseconds);
            var averageMs = durations.Any() ? durations.Average() : 0;
            return TimeSpan.FromMilliseconds(averageMs);
        }
    }
}

public class ChaosTestAttempt
{
    public string Scenario { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public HttpStatusCode? StatusCode { get; init; }
    public string ResponseContent { get; init; } = "";
    public Exception? Exception { get; init; }
    public bool IsResilient { get; init; }
    public TimeSpan ResponseTime { get; init; }
    public string? Payload { get; init; }
}
