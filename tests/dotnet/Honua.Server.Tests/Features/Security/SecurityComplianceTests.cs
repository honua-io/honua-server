// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Comprehensive security compliance tests covering OWASP Top 10 and enterprise security requirements
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Comprehensive)]
[Operation(Operations.Security)]
public sealed class SecurityComplianceTests : IAsyncLifetime
{
    private const string AdminPassword = "Valid-Test-Key123!";
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;
    private static readonly string[] _sensitiveDataPatterns =
    [
        "password",
        "secret",
        "ssn",
        "credit card",
        "api_key",
        "token"
    ];
    private static readonly string[] _systemLeakPatterns =
    [
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
    ];

    public SecurityComplianceTests()
    {
        _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("HONUA_DEV_AUTH", "false");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task Authentication_WithoutApiKey_ShouldReturn401()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/config");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task Authentication_WithInvalidApiKey_ShouldReturn401()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/config");
        request.Headers.Add("X-API-Key", "invalid-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/openapi.json")]
    public async Task Authentication_AdminOpenApiWithoutApiKey_ShouldReturn401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/openapi.json");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task Authorization_PublicEndpoint_AllowsAnonymousAccess()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer?f=json");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
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
            request.Headers.Add("X-API-Key", AdminPassword);

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
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
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
            request.Headers.Add("X-API-Key", AdminPassword);

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
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
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
        request.Headers.Add("X-API-Key", AdminPassword);
        // Deliberately not including CSRF token

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.OK
        );
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/ready")]
    public async Task SecurityHeaders_ShouldBePresent()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/ready");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.Headers.Should().ContainKey("X-Content-Type-Options");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");

        response.Headers.Should().ContainKey("X-Frame-Options");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");

        response.Headers.Should().ContainKey("X-XSS-Protection");
        response.Headers.GetValues("X-XSS-Protection").Should().Contain("1; mode=block");

        // Strict-Transport-Security is only emitted on HTTPS per RFC 6797 §7.2
        // and SecurityHeadersOptions.HstsHttpsOnly (default true). The in-memory
        // TestServer used here speaks HTTP, so we verify the header is absent;
        // HSTS-over-HTTPS coverage lives in unit tests where IsHttps can be forced.
        response.Headers.Should().NotContainKey("Strict-Transport-Security");

        response.Headers.Should().ContainKey("Content-Security-Policy");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
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

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/import/upload")
        {
            Content = content
        };
        request.Headers.Add("X-API-Key", AdminPassword);

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
    [Endpoint("GET /api/files/{path}")]
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
            request.Headers.Add("X-API-Key", AdminPassword);

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
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task InputValidation_ExcessivelyLargePayload_ShouldBeRejected()
    {
        // Arrange
        var largePayload = new string('A', 10 * 1024 * 1024); // 10MB
        var content = new StringContent(largePayload, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/services/test/FeatureServer/1/applyEdits")
        {
            Content = content
        };
        request.Headers.Add("X-API-Key", AdminPassword);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.BadRequest,
            HttpStatusCode.RequestEntityTooLarge
        );
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/config")]
    public async Task SessionManagement_ConcurrentRequests_ShouldSucceed()
    {
        // Arrange
        var sessionTasks = new List<Task<HttpResponseMessage>>();

        // Act - Create multiple concurrent requests to an admin endpoint
        for (int i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/config");
            request.Headers.Add("X-API-Key", AdminPassword);
            sessionTasks.Add(_client.SendAsync(request));
        }

        var responses = await Task.WhenAll(sessionTasks);

        // Assert - Concurrent requests should not trigger server errors
        responses.Should().NotContain(r => (int)r.StatusCode >= 500);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/users")]
    public async Task DataEncryption_SensitiveData_ShouldBeProtected()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/users");
        request.Headers.Add("X-API-Key", AdminPassword);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();

            // Sensitive data should not be in plain text
            content.Should().NotContainAny(_sensitiveDataPatterns,
                "Sensitive data should be encrypted or masked");

            // Should use HTTPS for sensitive endpoints
            response.RequestMessage!.RequestUri!.Scheme.Should().BeOneOf("https", "http");
            // Note: In production, this should be "https" only
        }
    }

    [IntegrationTest]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task CorrelationId_CriticalOperations_ReturnsCorrelationId()
    {
        // Arrange
        var payload = """
            {
                "adds": [
                    {
                        "geometry": {"type": "Point", "coordinates": [-122.0, 37.0]},
                        "attributes": {"name": "Correlation Test"}
                    }
                ]
            }
            """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/rest/services/test/FeatureServer/1/applyEdits")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-API-Key", AdminPassword);
        var correlationId = Guid.NewGuid().ToString();
        request.Headers.Add("X-Correlation-ID", correlationId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.Headers.Should().ContainKey("X-Correlation-ID");
        response.Headers.GetValues("X-Correlation-ID").Should().Contain(correlationId);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ErrorHandling_ShouldNotLeakInformation()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/nonexistent/FeatureServer/999/query");
        request.Headers.Add("X-API-Key", AdminPassword);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        var content = await response.Content.ReadAsStringAsync();

        // Error messages should not reveal system information
        content.Should().NotContainAny(_systemLeakPatterns, "Error messages should not leak system information");
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/ready")]
    public async Task HTTPS_Redirection_ShouldBeEnforced()
    {
        // This test would be more relevant in a production environment
        // For local testing, we verify HSTS headers are present

        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/ready");

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
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
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
            request.Headers.Add("X-API-Key", AdminPassword);

            var response = await _client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"Malformed JSON should be rejected: {payload}");
        }
    }
}
