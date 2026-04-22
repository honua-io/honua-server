// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.TestKit.Fuzzing;

/// <summary>
/// Fuzzing test scenarios for discovering edge cases and vulnerabilities.
/// </summary>
public static class FuzzTestScenarios
{
    /// <summary>
    /// Fuzzes CQL2 filter expressions with random mutations.
    /// </summary>
    public static async Task<FuzzTestResult> FuzzCql2FilterExpressions(
        HttpClient client,
        string endpoint,
        int iterations = 100)
    {
        var results = new List<FuzzTestAttempt>();
        var validExpressions = new[]
        {
            "name = 'test'",
            "age > 18",
            "status IN ('active', 'pending')",
            "ST_INTERSECTS(geometry, POLYGON((0 0, 1 0, 1 1, 0 1, 0 0)))",
            "created_at BETWEEN '2023-01-01' AND '2023-12-31'",
            "(name = 'test' AND status = 'active') OR priority > 5"
        };

        var random = new Random(12345); // Fixed seed for reproducible results

        for (int i = 0; i < iterations; i++)
        {
            // Start with a valid expression
            var baseExpression = validExpressions[i % validExpressions.Length];

            // Apply random mutations
            var mutatedExpression = ApplyRandomMutations(baseExpression, random, mutationCount: random.Next(1, 5));

            try
            {
                var encodedFilter = Uri.EscapeDataString(mutatedExpression);
                var url = $"{endpoint}?filter={encodedFilter}";

                var response = await client.GetAsync(url);

                results.Add(new FuzzTestAttempt
                {
                    Input = mutatedExpression,
                    StatusCode = response.StatusCode,
                    ResponseContent = await response.Content.ReadAsStringAsync(),
                    Exception = null,
                    IsValidResponse = IsValidFuzzResponse(response)
                });
            }
            catch (Exception ex)
            {
                results.Add(new FuzzTestAttempt
                {
                    Input = mutatedExpression,
                    Exception = ex,
                    IsValidResponse = IsValidFuzzException(ex)
                });
            }
        }

        return new FuzzTestResult
        {
            TestType = "CQL2 Filter Fuzzing",
            Endpoint = endpoint,
            Iterations = iterations,
            Attempts = results
        };
    }

    /// <summary>
    /// Fuzzes JSON input payloads with structure mutations.
    /// </summary>
    public static async Task<FuzzTestResult> FuzzJsonPayloads(
        HttpClient client,
        string endpoint,
        HttpMethod method,
        int iterations = 100)
    {
        var results = new List<FuzzTestAttempt>();
        var validJsonTemplates = new[]
        {
            """{"name": "test", "value": 123, "active": true}""",
            """{"geometry": {"type": "Point", "coordinates": [0, 0]}, "properties": {"id": 1}}""",
            """{"features": [{"type": "Feature", "geometry": null, "properties": {}}]}""",
            """{"type": "FeatureCollection", "features": []}"""
        };

        var random = new Random(12345);

        for (int i = 0; i < iterations; i++)
        {
            var baseJson = validJsonTemplates[i % validJsonTemplates.Length];
            var mutatedJson = ApplyJsonMutations(baseJson, random);

            try
            {
                var content = new StringContent(mutatedJson, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(method, endpoint) { Content = content };
                var response = await client.SendAsync(request);

                results.Add(new FuzzTestAttempt
                {
                    Input = mutatedJson,
                    StatusCode = response.StatusCode,
                    ResponseContent = await response.Content.ReadAsStringAsync(),
                    IsValidResponse = IsValidFuzzResponse(response)
                });
            }
            catch (Exception ex)
            {
                results.Add(new FuzzTestAttempt
                {
                    Input = mutatedJson,
                    Exception = ex,
                    IsValidResponse = IsValidFuzzException(ex)
                });
            }
        }

        return new FuzzTestResult
        {
            TestType = "JSON Payload Fuzzing",
            Endpoint = endpoint,
            Iterations = iterations,
            Attempts = results
        };
    }

    /// <summary>
    /// Fuzzes URL parameters with random values and structures.
    /// </summary>
    public static async Task<FuzzTestResult> FuzzUrlParameters(
        HttpClient client,
        string baseEndpoint,
        string[] parameterNames,
        int iterations = 100)
    {
        var results = new List<FuzzTestAttempt>();
        var random = new Random(12345);

        for (int i = 0; i < iterations; i++)
        {
            var url = baseEndpoint;
            var parameters = new List<string>();

            // Randomly select and mutate parameters
            foreach (var paramName in parameterNames)
            {
                if (random.NextDouble() < 0.7) // 70% chance to include each parameter
                {
                    var paramValue = GenerateRandomParameterValue(random);
                    parameters.Add($"{paramName}={Uri.EscapeDataString(paramValue)}");
                }
            }

            if (parameters.Count > 0)
            {
                url += "?" + string.Join("&", parameters);
            }

            try
            {
                var response = await client.GetAsync(url);

                results.Add(new FuzzTestAttempt
                {
                    Input = url,
                    StatusCode = response.StatusCode,
                    ResponseContent = await response.Content.ReadAsStringAsync(),
                    IsValidResponse = IsValidFuzzResponse(response)
                });
            }
            catch (Exception ex)
            {
                results.Add(new FuzzTestAttempt
                {
                    Input = url,
                    Exception = ex,
                    IsValidResponse = IsValidFuzzException(ex)
                });
            }
        }

        return new FuzzTestResult
        {
            TestType = "URL Parameter Fuzzing",
            Endpoint = baseEndpoint,
            Iterations = iterations,
            Attempts = results
        };
    }

    /// <summary>
    /// Fuzzes HTTP headers with various values and encodings.
    /// </summary>
    public static async Task<FuzzTestResult> FuzzHttpHeaders(
        HttpClient client,
        string endpoint,
        int iterations = 100)
    {
        var results = new List<FuzzTestAttempt>();
        var random = new Random(12345);

        var headerNames = new[]
        {
            "Accept", "Content-Type", "Authorization", "User-Agent",
            "Accept-Encoding", "Accept-Language", "X-Custom-Header",
            "Origin", "Referer", "Cache-Control"
        };

        for (int i = 0; i < iterations; i++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                // Add random headers
                var headerCount = random.Next(1, 5);
                var headerInput = new List<string>();

                for (int h = 0; h < headerCount; h++)
                {
                    var headerName = headerNames[random.Next(headerNames.Length)];
                    var headerValue = GenerateRandomHeaderValue(random);

                    try
                    {
                        request.Headers.Add(headerName, headerValue);
                        headerInput.Add($"{headerName}: {headerValue}");
                    }
                    catch (FormatException)
                    {
                        // Invalid header format - this is expected for some fuzz values
                        headerInput.Add($"{headerName}: {headerValue} (invalid)");
                    }
                }

                var response = await client.SendAsync(request);

                results.Add(new FuzzTestAttempt
                {
                    Input = string.Join(", ", headerInput),
                    StatusCode = response.StatusCode,
                    ResponseContent = await response.Content.ReadAsStringAsync(),
                    IsValidResponse = IsValidFuzzResponse(response)
                });
            }
            catch (Exception ex)
            {
                results.Add(new FuzzTestAttempt
                {
                    Input = "Header fuzzing iteration " + i,
                    Exception = ex,
                    IsValidResponse = IsValidFuzzException(ex)
                });
            }
        }

        return new FuzzTestResult
        {
            TestType = "HTTP Header Fuzzing",
            Endpoint = endpoint,
            Iterations = iterations,
            Attempts = results
        };
    }

    private static string ApplyRandomMutations(string input, Random random, int mutationCount)
    {
        var chars = input.ToCharArray();

        for (int i = 0; i < mutationCount && chars.Length > 0; i++)
        {
            var mutationType = random.Next(6);

            switch (mutationType)
            {
                case 0: // Character replacement
                    var pos = random.Next(chars.Length);
                    chars[pos] = GenerateRandomChar(random);
                    break;

                case 1: // Character insertion
                    var insertPos = random.Next(chars.Length + 1);
                    var newChars = new char[chars.Length + 1];
                    Array.Copy(chars, 0, newChars, 0, insertPos);
                    newChars[insertPos] = GenerateRandomChar(random);
                    Array.Copy(chars, insertPos, newChars, insertPos + 1, chars.Length - insertPos);
                    chars = newChars;
                    break;

                case 2: // Character deletion
                    if (chars.Length > 1)
                    {
                        var deletePos = random.Next(chars.Length);
                        var reducedChars = new char[chars.Length - 1];
                        Array.Copy(chars, 0, reducedChars, 0, deletePos);
                        Array.Copy(chars, deletePos + 1, reducedChars, deletePos, chars.Length - deletePos - 1);
                        chars = reducedChars;
                    }
                    break;

                case 3: // String duplication
                    return new string(chars) + new string(chars);

                case 4: // String truncation
                    if (chars.Length > 1)
                    {
                        var truncateLen = random.Next(1, chars.Length);
                        Array.Resize(ref chars, truncateLen);
                    }
                    break;

                case 5: // Special character injection
                    var specialChars = new[] { '\0', '\n', '\r', '\t', '\'', '"', '\\', '<', '>', '&' };
                    var specialPos = random.Next(chars.Length);
                    chars[specialPos] = specialChars[random.Next(specialChars.Length)];
                    break;
            }
        }

        return new string(chars);
    }

    private static string ApplyJsonMutations(string json, Random random)
    {
        var mutationType = random.Next(8);

        return mutationType switch
        {
            0 => json.Replace("\"", "'"), // Quote mutation
            1 => json.Replace("{", "{{").Replace("}", "}}"), // Brace duplication
            2 => json.Replace(",", ",,"), // Comma duplication
            3 => json.Replace(":", "::"), // Colon duplication
            4 => json[..^1], // Remove last character
            5 => json + json, // Duplication
            6 => json.Replace("true", "tru").Replace("false", "fals"), // Boolean corruption
            7 => json.Replace("null", "nul"), // Null corruption
            _ => ApplyRandomMutations(json, random, 3)
        };
    }

    private static string GenerateRandomParameterValue(Random random)
    {
        var valueType = random.Next(10);

        return valueType switch
        {
            0 => new string('x', random.Next(0, 1000)), // Long string
            1 => random.Next(-1000000, 1000000).ToString(CultureInfo.InvariantCulture), // Random number
            2 => "", // Empty string
            3 => "null",
            4 => "undefined",
            5 => new string((char)random.Next(32, 127), random.Next(1, 50)), // Random ASCII
            6 => Convert.ToBase64String(Encoding.UTF8.GetBytes("test data")),
            7 => Uri.EscapeDataString("test with spaces & symbols!@#$%^&*()"),
            8 => string.Join("", Enumerable.Range(0, 20).Select(_ => random.Next(10).ToString(CultureInfo.InvariantCulture))),
            _ => "test" + random.Next(1000).ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string GenerateRandomHeaderValue(Random random)
    {
        var valueType = random.Next(8);

        return valueType switch
        {
            0 => new string('A', random.Next(1, 1000)),
            1 => "application/json; charset=" + new string((char)random.Next(32, 127), 10),
            2 => "",
            3 => "\0\n\r\t",
            4 => "Bearer " + Convert.ToBase64String(Encoding.UTF8.GetBytes("random token")),
            5 => "💀🚀✨", // Unicode
            6 => "../../../etc/passwd",
            _ => $"fuzz-value-{random.Next(1000)}"
        };
    }

    private static char GenerateRandomChar(Random random)
    {
        var charType = random.Next(5);

        return charType switch
        {
            0 => (char)random.Next(32, 127), // Printable ASCII
            1 => (char)random.Next(0, 32), // Control characters
            2 => (char)random.Next(127, 256), // Extended ASCII
            3 => (char)random.Next(256, 65536), // Unicode
            _ => (char)random.Next(0, 256) // Any byte value
        };
    }

    private static bool IsValidFuzzResponse(HttpResponseMessage response)
    {
        // Valid responses should be 2xx, 4xx (client error), or specific 5xx codes
        var statusCode = (int)response.StatusCode;
        return statusCode < 500 || statusCode == 503; // Allow service unavailable
    }

    private static bool IsValidFuzzException(Exception exception)
    {
        // These exceptions are acceptable during fuzzing
        return exception is ArgumentException or
               FormatException or
               InvalidOperationException or
               HttpRequestException or
               TaskCanceledException;
    }
}

public class FuzzTestResult
{
    public string TestType { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public int Iterations { get; init; }
    public List<FuzzTestAttempt> Attempts { get; init; } = new();

    public int SuccessfulAttempts => Attempts.Count(a => a.IsValidResponse);
    public int FailedAttempts => Attempts.Count(a => !a.IsValidResponse);
    public double SuccessRate => Iterations > 0 ? (double)SuccessfulAttempts / Iterations : 1.0;

    public IEnumerable<FuzzTestAttempt> CriticalFailures =>
        Attempts.Where(a => !a.IsValidResponse && a.StatusCode == System.Net.HttpStatusCode.InternalServerError);
}

public class FuzzTestAttempt
{
    public string Input { get; init; } = "";
    public System.Net.HttpStatusCode? StatusCode { get; init; }
    public string ResponseContent { get; init; } = "";
    public Exception? Exception { get; init; }
    public bool IsValidResponse { get; init; }
}
