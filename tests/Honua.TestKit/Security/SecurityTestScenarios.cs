// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.TestKit.Security;

/// <summary>
/// Security testing scenarios for API endpoints.
/// Tests common vulnerabilities and attack vectors.
/// </summary>
public static class SecurityTestScenarios
{
    /// <summary>
    /// SQL injection test payloads for filter parameters.
    /// </summary>
    public static readonly string[] SqlInjectionPayloads = {
        "'; DROP TABLE users; --",
        "' OR 1=1 --",
        "'; INSERT INTO layers (name) VALUES ('malicious'); --",
        "' UNION SELECT password FROM users --",
        "admin'--",
        "admin' #",
        "admin'/*",
        "' or 1=1#",
        "' or 1=1--",
        "' or 1=1/*",
        ") or '1'='1--",
        ") or ('1'='1--"
    };

    /// <summary>
    /// XSS attack payloads for text fields.
    /// </summary>
    public static readonly string[] XssPayloads = {
        "<script>alert('xss')</script>",
        "<img src=x onerror=alert('xss')>",
        "javascript:alert('xss')",
        "<svg onload=alert('xss')>",
        "'\"><script>alert('xss')</script>",
        "<iframe src=javascript:alert('xss')></iframe>",
        "<body onload=alert('xss')>",
        "<details open ontoggle=alert('xss')>"
    };

    /// <summary>
    /// Path traversal payloads for file access attempts.
    /// </summary>
    public static readonly string[] PathTraversalPayloads = {
        "../../../etc/passwd",
        "..\\..\\..\\windows\\system32\\config\\sam",
        "%2e%2e%2f%2e%2e%2f%2e%2e%2fetc%2fpasswd",
        "....//....//....//etc/passwd",
        "../../../../../../../../../../etc/passwd%00.jpg",
        "/var/log/apache2/access.log",
        "\\..\\..\\..\\windows\\system32\\drivers\\etc\\hosts"
    };

    /// <summary>
    /// NoSQL injection payloads.
    /// </summary>
    public static readonly string[] NoSqlInjectionPayloads = {
        "'; return true; //",
        "{'$gt':''}",
        "{'$ne':null}",
        "{'$regex':'.*'}",
        "'; var date = new Date(); do{curDate = new Date();}while(curDate-date<10000); return false; //",
        "'; db.dropDatabase(); //"
    };

    /// <summary>
    /// LDAP injection payloads.
    /// </summary>
    public static readonly string[] LdapInjectionPayloads = {
        "*)(uid=*",
        "*)(|(mail=*))",
        "*)(|(password=*",
        "admin)(&(password=*",
        "*))%00"
    };

    /// <summary>
    /// Tests CQL2 filter endpoints for SQL injection vulnerabilities.
    /// </summary>
    public static async Task<SecurityTestResult> TestCql2SqlInjection(
        HttpClient client,
        string endpoint,
        string parameterName = "filter")
    {
        var results = new List<SecurityTestAttempt>();

        foreach (var payload in SqlInjectionPayloads)
        {
            try
            {
                var encodedPayload = Uri.EscapeDataString(payload);
                var url = $"{endpoint}?{parameterName}={encodedPayload}";

                var response = await client.GetAsync(url);

                var responseContent = await response.Content.ReadAsStringAsync();
                results.Add(new SecurityTestAttempt
                {
                    Payload = payload,
                    StatusCode = response.StatusCode,
                    ResponseContent = responseContent,
                    IsSafe = IsResponseSafe(response.StatusCode, responseContent, payload)
                });
            }
            catch (Exception ex)
            {
                results.Add(new SecurityTestAttempt
                {
                    Payload = payload,
                    Exception = ex,
                    IsSafe = true // Exception is better than successful injection
                });
            }
        }

        return new SecurityTestResult
        {
            TestType = "SQL Injection",
            Endpoint = endpoint,
            Attempts = results
        };
    }

    /// <summary>
    /// Tests endpoints for XSS vulnerabilities in responses.
    /// </summary>
    public static async Task<SecurityTestResult> TestXssVulnerability(
        HttpClient client,
        string endpoint,
        string parameterName)
    {
        var results = new List<SecurityTestAttempt>();

        foreach (var payload in XssPayloads)
        {
            try
            {
                var encodedPayload = Uri.EscapeDataString(payload);
                var url = $"{endpoint}?{parameterName}={encodedPayload}";

                var response = await client.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                results.Add(new SecurityTestAttempt
                {
                    Payload = payload,
                    StatusCode = response.StatusCode,
                    ResponseContent = content,
                    IsSafe = !content.Contains(payload, StringComparison.OrdinalIgnoreCase)
                });
            }
            catch (Exception ex)
            {
                results.Add(new SecurityTestAttempt
                {
                    Payload = payload,
                    Exception = ex,
                    IsSafe = true
                });
            }
        }

        return new SecurityTestResult
        {
            TestType = "XSS",
            Endpoint = endpoint,
            Attempts = results
        };
    }

    /// <summary>
    /// Tests authorization boundaries and privilege escalation.
    /// </summary>
    public static async Task<SecurityTestResult> TestAuthorizationBypass(
        HttpClient client,
        string[] protectedEndpoints,
        Dictionary<string, string>? headers = null)
    {
        var results = new List<SecurityTestAttempt>();

        foreach (var endpoint in protectedEndpoints)
        {
            // Test without authentication
            var response = await client.GetAsync(endpoint);

            results.Add(new SecurityTestAttempt
            {
                Payload = "No authentication",
                StatusCode = response.StatusCode,
                ResponseContent = await response.Content.ReadAsStringAsync(),
                IsSafe = response.StatusCode == HttpStatusCode.Unauthorized ||
                        response.StatusCode == HttpStatusCode.Forbidden
            });

            // Test with malformed headers
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    var malformedValues = new[]
                    {
                        "",
                        "invalid-token",
                        "Bearer ",
                        "Bearer invalid",
                        "../admin",
                        "null",
                        "undefined"
                    };

                    foreach (var value in malformedValues)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                        request.Headers.Add(header.Key, value);

                        var malformedResponse = await client.SendAsync(request);

                        results.Add(new SecurityTestAttempt
                        {
                            Payload = $"{header.Key}: {value}",
                            StatusCode = malformedResponse.StatusCode,
                            ResponseContent = await malformedResponse.Content.ReadAsStringAsync(),
                            IsSafe = malformedResponse.StatusCode == HttpStatusCode.Unauthorized ||
                                    malformedResponse.StatusCode == HttpStatusCode.Forbidden
                        });
                    }
                }
            }
        }

        return new SecurityTestResult
        {
            TestType = "Authorization Bypass",
            Endpoint = string.Join(", ", protectedEndpoints),
            Attempts = results
        };
    }

    /// <summary>
    /// Tests for CORS misconfigurations.
    /// </summary>
    public static async Task<SecurityTestResult> TestCorsConfiguration(
        HttpClient client,
        string endpoint)
    {
        var results = new List<SecurityTestAttempt>();

        var maliciousOrigins = new[]
        {
            "https://evil.com",
            "http://attacker.evil.com",
            "javascript:alert('xss')",
            "data:text/html,<script>alert('xss')</script>",
            "null",
            "*"
        };

        foreach (var origin in maliciousOrigins)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, endpoint);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");

            var response = await client.SendAsync(request);
            var responseHeaders = response.Headers.ToString();

            var isSafe = !responseHeaders.Contains("Access-Control-Allow-Origin: *") &&
                        !responseHeaders.Contains($"Access-Control-Allow-Origin: {origin}");

            results.Add(new SecurityTestAttempt
            {
                Payload = origin,
                StatusCode = response.StatusCode,
                ResponseContent = responseHeaders,
                IsSafe = isSafe
            });
        }

        return new SecurityTestResult
        {
            TestType = "CORS Configuration",
            Endpoint = endpoint,
            Attempts = results
        };
    }

    /// <summary>
    /// Tests rate limiting effectiveness.
    /// </summary>
    public static async Task<SecurityTestResult> TestRateLimiting(
        HttpClient client,
        string endpoint,
        int requestCount = 100,
        TimeSpan duration = default)
    {
        if (duration == default)
        {
            duration = TimeSpan.FromSeconds(10);
        }

        var results = new List<SecurityTestAttempt>();
        var startTime = DateTime.UtcNow;
        var tasks = new List<Task<HttpResponseMessage>>();

        // Fire requests rapidly
        for (int i = 0; i < requestCount; i++)
        {
            tasks.Add(client.GetAsync(endpoint));
        }

        var responses = await Task.WhenAll(tasks);
        var rateLimitedCount = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        results.Add(new SecurityTestAttempt
        {
            Payload = $"{requestCount} requests in {duration.TotalSeconds}s",
            StatusCode = HttpStatusCode.OK,
            ResponseContent = $"Rate limited: {rateLimitedCount}/{requestCount}",
            IsSafe = rateLimitedCount > 0 // Rate limiting should kick in
        });

        return new SecurityTestResult
        {
            TestType = "Rate Limiting",
            Endpoint = endpoint,
            Attempts = results
        };
    }

    private static bool IsResponseSafe(HttpStatusCode statusCode, string responseContent, string payload)
    {
        // If request was rejected (4xx/5xx), it's likely safe
        if ((int)statusCode >= 400)
            return true;

        // Check if payload appears unescaped in response
        return !responseContent.Contains(payload, StringComparison.OrdinalIgnoreCase);
    }
}

public class SecurityTestResult
{
    public string TestType { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public List<SecurityTestAttempt> Attempts { get; init; } = new();

    public bool AllSafe => Attempts.All(a => a.IsSafe);
    public int VulnerableAttempts => Attempts.Count(a => !a.IsSafe);
    public double SafetyScore => Attempts.Count > 0 ? (double)Attempts.Count(a => a.IsSafe) / Attempts.Count : 1.0;
}

public class SecurityTestAttempt
{
    public string Payload { get; init; } = "";
    public HttpStatusCode? StatusCode { get; init; }
    public string ResponseContent { get; init; } = "";
    public Exception? Exception { get; init; }
    public bool IsSafe { get; init; }
}
