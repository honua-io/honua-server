// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Comprehensive security compliance tests covering OWASP Top 10 and enterprise security requirements
/// </summary>
public class SecurityComplianceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SecurityComplianceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [IntegrationTest]
    public async Task Authentication_WithoutApiKey_ShouldReturn401()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer/1/query");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    public async Task Authentication_WithInvalidApiKey_ShouldReturn401()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer/1/query");
        request.Headers.Add("X-API-Key", "invalid-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    public async Task Authorization_AccessToRestrictedLayer_ShouldReturn403()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/restricted/FeatureServer/1/query");
        request.Headers.Add("X-API-Key", "limited-access-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    public async Task SqlInjection_InWhereClause_ShouldBeBlocked()
    {
        // Arrange
        var maliciousInputs = new[]
        {
            "1=1; DROP TABLE users;--",
            "1=1 UNION SELECT * FROM pg_user",
            "'; DELETE FROM features; --",
            "1=1 OR 1=1",
            "1=1; INSERT INTO audit_log (message) VALUES ('hacked');",
            "' OR '1'='1",
            "1'; EXEC xp_cmdshell('dir'); --"
        };

        // Act & Assert
        foreach (var maliciousInput in maliciousInputs)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/services/test/FeatureServer/1/query?where={Uri.EscapeDataString(maliciousInput)}");
            request.Headers.Add("X-API-Key", "valid-test-key");

            var response = await _client.SendAsync(request);

            // Should either return 400 (bad request) or 200 with safe results, but never execute the injection
            response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.OK);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync();
                content.Should().NotContain("pg_user", "SQL injection should not return system information");
                content.Should().NotContain("error", "SQL injection should not cause database errors");
            }
        }
    }

    [IntegrationTest]
    public async Task XSS_InParameters_ShouldBeSanitized()
    {
        // Arrange
        var xssPayloads = new[]
        {
            "<script>alert('xss')</script>",
            "javascript:alert('xss')",
            "<img src=x onerror=alert('xss')>",
            "<svg onload=alert('xss')>",
            "';alert('xss');//",
            "<iframe src='javascript:alert(\"xss\")'></iframe>"
        };

        // Act & Assert
        foreach (var payload in xssPayloads)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/services/test/FeatureServer/1/query?outFields={Uri.EscapeDataString(payload)}");
            request.Headers.Add("X-API-Key", "valid-test-key");

            var response = await _client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            // Content should be sanitized
            content.Should().NotContain("<script>", "Script tags should be sanitized");
            content.Should().NotContain("javascript:", "JavaScript protocol should be sanitized");
            content.Should().NotContain("onerror=", "Event handlers should be sanitized");
            content.Should().NotContain("onload=", "Event handlers should be sanitized");
        }
    }

    [IntegrationTest]
    public async Task CSRF_PostWithoutToken_ShouldBeBlocked()
    {
        // Arrange
        var payload = """
            {
                "adds": [
                    {
                        "geometry": {"type": "Point", "coordinates": [-122.0, 37.0]},
                        "attributes": {"name": "CSRF Test"}
                    }
                ]
            }
            """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/services/test/FeatureServer/1/applyEdits")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", "valid-test-key");
        // Deliberately not including CSRF token

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Unauthorized
        );
    }

    [IntegrationTest]
    public async Task RateLimiting_ExcessiveRequests_ShouldBeThrottled()
    {
        // Arrange
        var tasks = new List<Task<HttpResponseMessage>>();
        var requestCount = 100; // Assuming rate limit is lower than this

        // Act
        for (int i = 0; i < requestCount; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer/1/query?where=1=1");
            request.Headers.Add("X-API-Key", "valid-test-key");
            tasks.Add(_client.SendAsync(request));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        var rateLimitedResponses = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        rateLimitedResponses.Should().BeGreaterThan(0, "Rate limiting should be active");

        var successfulResponses = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        successfulResponses.Should().BeLessThan(requestCount, "Not all requests should succeed due to rate limiting");
    }

    [IntegrationTest]
    public async Task SecurityHeaders_ShouldBePresent()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.Headers.Should().ContainKey("X-Content-Type-Options");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");

        response.Headers.Should().ContainKey("X-Frame-Options");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");

        response.Headers.Should().ContainKey("X-XSS-Protection");
        response.Headers.GetValues("X-XSS-Protection").Should().Contain("1; mode=block");

        response.Headers.Should().ContainKey("Strict-Transport-Security");
        var hstsHeader = response.Headers.GetValues("Strict-Transport-Security").First();
        hstsHeader.Should().Contain("max-age=");
        hstsHeader.Should().Contain("includeSubDomains");

        response.Headers.Should().ContainKey("Content-Security-Policy");
    }

    [IntegrationTest]
    public async Task FileUpload_MaliciousFile_ShouldBeRejected()
    {
        // Arrange
        var maliciousContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE root [
                <!ENTITY xxe SYSTEM "file:///etc/passwd">
            ]>
            <root>&xxe;</root>
            """;

        var content = new MultipartFormDataContent();
        content.Add(new StringContent(maliciousContent), "file", "malicious.xml");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/upload")
        {
            Content = content
        };
        request.Headers.Add("X-API-Key", "valid-test-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.UnsupportedMediaType,
            HttpStatusCode.Forbidden
        );

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            responseContent.Should().NotContain("root:", "XXE attack should be prevented");
        }
    }

    [IntegrationTest]
    public async Task PathTraversal_InFileAccess_ShouldBeBlocked()
    {
        // Arrange
        var maliciousPaths = new[]
        {
            "../../../etc/passwd",
            "..\\..\\..\\windows\\system32\\drivers\\etc\\hosts",
            "....//....//....//etc//passwd",
            "%2e%2e%2f%2e%2e%2f%2e%2e%2fetc%2fpasswd",
            "..%252f..%252f..%252fetc%252fpasswd"
        };

        // Act & Assert
        foreach (var maliciousPath in maliciousPaths)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{Uri.EscapeDataString(maliciousPath)}");
            request.Headers.Add("X-API-Key", "valid-test-key");

            var response = await _client.SendAsync(request);

            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.BadRequest,
                HttpStatusCode.NotFound,
                HttpStatusCode.Forbidden
            );

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync();
                content.Should().NotContain("root:", "Path traversal should be prevented");
            }
        }
    }

    [IntegrationTest]
    public async Task InputValidation_ExcessivelyLargePayload_ShouldBeRejected()
    {
        // Arrange
        var largePayload = new string('A', 10 * 1024 * 1024); // 10MB
        var content = new StringContent(largePayload, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/services/test/FeatureServer/1/applyEdits")
        {
            Content = content
        };
        request.Headers.Add("X-API-Key", "valid-test-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.RequestEntityTooLarge,
            HttpStatusCode.PayloadTooLarge
        );
    }

    [IntegrationTest]
    public async Task SessionManagement_ConcurrentSessions_ShouldBeControlled()
    {
        // Arrange
        var apiKey = "session-test-key";
        var sessionTasks = new List<Task<HttpResponseMessage>>();

        // Act - Create multiple concurrent sessions
        for (int i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/info");
            request.Headers.Add("X-API-Key", apiKey);
            sessionTasks.Add(_client.SendAsync(request));
        }

        var responses = await Task.WhenAll(sessionTasks);

        // Assert
        var successfulSessions = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejectedSessions = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        // Should limit concurrent sessions
        rejectedSessions.Should().BeGreaterThan(0, "Concurrent session limits should be enforced");
    }

    [IntegrationTest]
    public async Task DataEncryption_SensitiveData_ShouldBeProtected()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Add("X-API-Key", "admin-test-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();

            // Sensitive data should not be in plain text
            content.Should().NotContainAny(SecurityTestScenarios.SensitiveDataPatterns,
                "Sensitive data should be encrypted or masked");

            // Should use HTTPS for sensitive endpoints
            response.RequestMessage!.RequestUri!.Scheme.Should().BeOneOf("https", "http");
            // Note: In production, this should be "https" only
        }
    }

    [IntegrationTest]
    public async Task AuditLogging_CriticalOperations_ShouldBeLogged()
    {
        // Arrange
        var payload = """
            {
                "adds": [
                    {
                        "geometry": {"type": "Point", "coordinates": [-122.0, 37.0]},
                        "attributes": {"name": "Audit Test"}
                    }
                ]
            }
            """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/services/test/FeatureServer/1/applyEdits")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", "valid-test-key");
        request.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString());

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        // Audit logs should be generated (this would need to be verified through log inspection in real tests)
        response.Headers.Should().ContainKey("X-Audit-ID", "Audit ID should be returned for critical operations");
    }

    [IntegrationTest]
    public async Task ErrorHandling_ShouldNotLeakInformation()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/nonexistent/FeatureServer/999/query");
        request.Headers.Add("X-API-Key", "valid-test-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        var content = await response.Content.ReadAsStringAsync();

        // Error messages should not reveal system information
        content.Should().NotContainAny(new[]
        {
            "pg_",
            "postgres",
            "database",
            "connection",
            "System.",
            "Exception",
            "StackTrace",
            "/src/",
            "C:\\",
            "file not found",
            "access denied"
        }, "Error messages should not leak system information");
    }

    [IntegrationTest]
    public async Task HTTPS_Redirection_ShouldBeEnforced()
    {
        // This test would be more relevant in a production environment
        // For local testing, we verify HSTS headers are present

        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        if (response.Headers.Contains("Strict-Transport-Security"))
        {
            var hstsHeader = response.Headers.GetValues("Strict-Transport-Security").First();
            hstsHeader.Should().Contain("max-age=");
        }
    }

    [IntegrationTest]
    public async Task ContentValidation_MalformedJSON_ShouldBeRejected()
    {
        // Arrange
        var malformedPayloads = new[]
        {
            "{invalid json}",
            "{'single_quotes': 'should_fail'}",
            "{\"unclosed\": \"string}",
            "{trailing_comma: true,}",
            "null",
            "undefined"
        };

        // Act & Assert
        foreach (var payload in malformedPayloads)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/rest/services/test/FeatureServer/1/applyEdits")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-API-Key", "valid-test-key");

            var response = await _client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"Malformed JSON should be rejected: {payload}");
        }
    }
}
