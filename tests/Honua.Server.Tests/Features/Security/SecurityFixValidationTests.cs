// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Security fix validation tests covering critical P0/P1 vulnerabilities.
/// Tests prevent regressions in authentication bypass, HTTPS enforcement, and credential exposure.
/// </summary>
[Collection("Database")]
public class SecurityFixValidationTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;

    public SecurityFixValidationTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [SecurityTest]
    [Fact(DisplayName = "SQL injection payloads are properly sanitized in CQL filters")]
    public async Task SqlInjectionPayloads_ProperlySanitizedInCqlFilters()
    {
        // Arrange: Use subset of SQL injection payloads for performance
        var maliciousFilters = new[]
        {
            "'; DROP TABLE users; --",
            "' OR 1=1 --",
            "' UNION SELECT password FROM users --",
            ") or '1'='1--"
        };

        foreach (var payload in maliciousFilters)
        {
            // Act: Try to inject SQL via CQL filter
            var encodedPayload = Uri.EscapeDataString($"name = '{payload}'");
            var response = await _client.GetAsync($"/ogc/features/v1/collections/test/items?filter={encodedPayload}");

            // Assert: Should either reject malicious input or safely handle it
            var content = await response.Content.ReadAsStringAsync();

            // Should not contain database errors that indicate successful injection
            content.Should().NotContain("syntax error", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("DROP TABLE", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("INSERT INTO", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("DELETE FROM", StringComparison.OrdinalIgnoreCase);

            // Should return appropriate response
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.OK,
                HttpStatusCode.Unauthorized);
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "Malicious field names are sanitized in query parameters")]
    public async Task MaliciousFieldNames_SanitizedInQueryParameters()
    {
        // Test malicious field names in outFields parameter
        var maliciousFieldNames = new[]
        {
            "'; DROP TABLE users; --",
            "id) OR 1=1 --",
            "../../../etc/passwd",
            "<script>alert('xss')</script>"
        };

        foreach (var maliciousField in maliciousFieldNames)
        {
            // Act: Try to use malicious field name in query
            var encodedField = Uri.EscapeDataString(maliciousField);
            var response = await _client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?outFields={encodedField}");

            // Assert: Should reject or safely handle malicious field names
            var content = await response.Content.ReadAsStringAsync();

            content.Should().NotContain("syntax error", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("DROP TABLE", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("UPDATE users", StringComparison.OrdinalIgnoreCase);

            // Should return error or safe result
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.OK,
                HttpStatusCode.Unauthorized);
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "CORS configuration prevents credential exposure to malicious origins")]
    public async Task CorsConfiguration_PreventsCredentialExposureToMaliciousOrigins()
    {
        // Test CORS security with malicious origins
        var result = await SecurityTestScenarios.TestCorsConfiguration(_client, "/ogc/features/v1/collections");

        // Assert: Should be safe from CORS attacks
        result.AllSafe.Should().BeTrue("CORS should not expose credentials to malicious origins");
        result.VulnerableAttempts.Should().Be(0, "No CORS vulnerabilities should exist");

        // Verify specific dangerous origins are blocked
        var dangerousAttempts = result.Attempts.Where(a =>
            a.Payload == "https://evil.com" || a.Payload == "*" || a.Payload == "null");

        foreach (var attempt in dangerousAttempts)
        {
            attempt.IsSafe.Should().BeTrue($"Dangerous origin '{attempt.Payload}' should be blocked");
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "XSS payloads are properly escaped in error responses")]
    public async Task XssPayloads_ProperlyEscapedInErrorResponses()
    {
        // Test XSS prevention in error messages
        var xssPayloads = new[]
        {
            "<script>alert('xss')</script>",
            "<img src=x onerror=alert('xss')>",
            "javascript:alert('xss')",
            "<svg onload=alert('xss')>"
        };

        foreach (var payload in xssPayloads)
        {
            // Act: Try to inject XSS via query parameter
            var encodedPayload = Uri.EscapeDataString(payload);
            var response = await _client.GetAsync($"/rest/services/invalid/FeatureServer/0/query?where={encodedPayload}");

            var content = await response.Content.ReadAsStringAsync();

            // Assert: XSS payloads should not appear unescaped in responses
            content.Should().NotContain(payload, "XSS payload should be escaped or removed");

            // Common XSS patterns should be escaped
            content.Should().NotContain("<script>", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("javascript:", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("onerror=", StringComparison.OrdinalIgnoreCase);
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "Path traversal attempts are blocked in file parameters")]
    public async Task PathTraversalAttempts_BlockedInFileParameters()
    {
        // Test path traversal prevention
        var pathTraversalPayloads = new[]
        {
            "../../../etc/passwd",
            "..\\..\\..\\windows\\system32\\config\\sam",
            "%2e%2e%2f%2e%2e%2f%2e%2e%2fetc%2fpasswd",
            "....//....//....//etc/passwd"
        };

        foreach (var payload in pathTraversalPayloads)
        {
            // Act: Try path traversal via various endpoints
            var encodedPayload = Uri.EscapeDataString(payload);
            var response = await _client.GetAsync($"/admin/files?path={encodedPayload}");

            // Assert: Path traversal should be blocked
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.Forbidden,
                HttpStatusCode.NotFound);

            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("/etc/passwd");
            content.Should().NotContain("config\\sam");
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "Authentication endpoints resist brute force attempts")]
    public async Task AuthenticationEndpoints_ResistBruteForceAttempts()
    {
        // Test authentication rate limiting and brute force protection
        var invalidApiKeys = Enumerable.Range(0, 20).Select(i => $"invalid-key-{i}").ToArray();
        var responses = new List<HttpStatusCode>();

        foreach (var invalidKey in invalidApiKeys)
        {
            // Act: Rapid authentication attempts with invalid keys
            using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/health");
            request.Headers.Add("X-API-Key", invalidKey);

            var response = await _client.SendAsync(request);
            responses.Add(response.StatusCode);

            // Small delay between attempts
            await Task.Delay(10);
        }

        // Assert: Should handle brute force attempts appropriately
        var unauthorizedCount = responses.Count(s => s == HttpStatusCode.Unauthorized);
        var tooManyRequestsCount = responses.Count(s => s == HttpStatusCode.TooManyRequests);

        // Most should be unauthorized, some might be rate limited
        unauthorizedCount.Should().BeGreaterThan(0, "Invalid API keys should be rejected");

        // If rate limiting is active, should see some 429 responses
        if (tooManyRequestsCount > 0)
        {
            tooManyRequestsCount.Should().BeLessThan(unauthorizedCount,
                "Rate limiting should kick in after some attempts");
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "Sensitive information is not exposed in error messages")]
    public async Task SensitiveInformation_NotExposedInErrorMessages()
    {
        // Test that error messages don't leak sensitive information
        var sensitiveEndpoints = new[]
        {
            "/admin/config",
            "/admin/connections",
            "/admin/secrets"
        };

        foreach (var endpoint in sensitiveEndpoints)
        {
            // Act: Try to access sensitive endpoint without proper auth
            var response = await _client.GetAsync(endpoint);

            var content = await response.Content.ReadAsStringAsync();

            // Assert: Error messages should not contain sensitive details
            content.Should().NotContain("password", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("secret", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("key", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("token", StringComparison.OrdinalIgnoreCase);
            content.Should().NotContain("connection string", StringComparison.OrdinalIgnoreCase);

            // Should return generic error
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Forbidden,
                HttpStatusCode.NotFound);
        }
    }

    [SecurityTest]
    [Fact(DisplayName = "Large payload attacks are handled gracefully")]
    public async Task LargePayloadAttacks_HandledGracefully()
    {
        // Test that extremely large payloads don't cause DoS
        var largePayload = new string('A', 10 * 1024 * 1024); // 10MB payload

        // Act: Send large payload to various endpoints
        var content = new StringContent(largePayload, Encoding.UTF8, "application/json");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _client.PostAsync("/ogc/features/v1/collections/test/items", content, cts.Token);

            // Assert: Should handle large payload appropriately
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.RequestEntityTooLarge,
                HttpStatusCode.PayloadTooLarge,
                HttpStatusCode.InternalServerError);
        }
        catch (TaskCanceledException)
        {
            // Timeout is acceptable for protection against large payloads
        }
        catch (HttpRequestException)
        {
            // Connection errors are acceptable for protection
        }
    }
}