// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Tests for the CSP violation report endpoint.
/// Verifies proper handling of CSP violation reports sent by browsers.
/// </summary>
[Collection("Database")]
[Trait("Category", "Integration")]
[Trait("Component", "Security")]
[Trait("Feature", "CspViolationReporting")]
[Protocol(TestProtocols.Comprehensive)]
[Operation(Operations.Security)]
public class CspViolationReportEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions _kebabCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower
    };

    public CspViolationReportEndpointTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithValidReport_Returns204NoContent()
    {
        // Arrange
        var violationReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://evil.example.com/malicious.js",
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'; script-src 'self'",
                DocumentUri = "https://app.example.com/page",
                SourceFile = "https://app.example.com/page",
                LineNumber = 42,
                ColumnNumber = 15
            }
        };

        var jsonContent = JsonSerializer.Serialize(violationReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithEmptyBody_Returns204NoContent()
    {
        // Arrange
        var content = new StringContent("", Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithInvalidJson_Returns204NoContent()
    {
        // Arrange - Invalid JSON should not crash the endpoint
        var content = new StringContent("{ invalid json }", Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithMalformedReport_Returns204NoContent()
    {
        // Arrange
        var malformedReport = new { random = "data", not = "csp-report" };
        var jsonContent = JsonSerializer.Serialize(malformedReport);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithSuspiciousViolation_Returns204NoContent()
    {
        // Arrange - Violation that appears to be from a malicious source
        var suspiciousReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "javascript:void(0)",
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'; script-src 'self'",
                DocumentUri = "https://app.example.com/page",
                SourceFile = "javascript:eval(malicious_code)",
                LineNumber = 1,
                ColumnNumber = 1
            }
        };

        var jsonContent = JsonSerializer.Serialize(suspiciousReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        // Note: In a production scenario, this would trigger additional security logging
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithCompleteViolationData_Returns204NoContent()
    {
        // Arrange - Complete violation report with all fields
        var completeReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://untrusted.cdn.com/library.js",
                ViolatedDirective = "script-src",
                OriginalPolicy = "default-src 'self'; script-src 'self' https://trusted.cdn.com",
                DocumentUri = "https://app.example.com/dashboard",
                SourceFile = "https://app.example.com/dashboard",
                LineNumber = 125,
                ColumnNumber = 34,
                EffectiveDirective = "script-src",
                Sample = "https://untrusted.cdn.com/library.js",
                StatusCode = 200
            }
        };

        var jsonContent = JsonSerializer.Serialize(completeReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithXForwardedForHeader_HandlesProxiedRequests()
    {
        // Arrange
        var violationReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://evil.example.com/script.js",
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'; script-src 'self'",
                DocumentUri = "https://app.example.com/page"
            }
        };

        var jsonContent = JsonSerializer.Serialize(violationReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Add forwarded header to simulate load balancer/proxy
        _client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.100, 10.0.0.1");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("https://chrome-extension://abcdef/script.js")]
    [InlineData("moz-extension://12345/content.js")]
    [InlineData("data:text/html,<script>alert('xss')</script>")]
    [InlineData("javascript:alert('xss')")]
    [InlineData("vbscript:msgbox('xss')")]
    public async Task CspViolationReport_WithSuspiciousUris_Returns204NoContent(string blockedUri)
    {
        // Arrange
        var suspiciousReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = blockedUri,
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'; script-src 'self'",
                DocumentUri = "https://app.example.com/page"
            }
        };

        var jsonContent = JsonSerializer.Serialize(suspiciousReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        // These URIs should trigger suspicious activity logging in production
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithoutAuthentication_AcceptsAnonymousRequests()
    {
        // Arrange - CSP reports come from browsers without authentication
        var violationReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://blocked.example.com/resource",
                ViolatedDirective = "img-src 'self'",
                OriginalPolicy = "default-src 'self'; img-src 'self'",
                DocumentUri = "https://app.example.com/page"
            }
        };

        var jsonContent = JsonSerializer.Serialize(violationReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act - No authentication headers
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithLargeReport_Returns204NoContent()
    {
        // Arrange - Large violation report to test size handling
        var largeReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://example.com/" + new string('x', 1000),
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'; script-src 'self' " + new string('x', 500),
                DocumentUri = "https://app.example.com/page",
                Sample = new string('x', 200)
            }
        };

        var jsonContent = JsonSerializer.Serialize(largeReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithIncorrectContentType_Returns204NoContent()
    {
        // Arrange - Browsers might send with different content types
        var violationReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://blocked.example.com/script.js",
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'; script-src 'self'",
                DocumentUri = "https://app.example.com/page"
            }
        };

        var jsonContent = JsonSerializer.Serialize(violationReport, _kebabCaseJsonOptions);

        var content = new StringContent(jsonContent, Encoding.UTF8, "application/csp-report");

        // Act
        var response = await _client.PostAsync("/csp-violation-report", content);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /csp-violation-report")]
    public async Task CspViolationReport_WithPayloadExceedingLimit_Returns204NoContent()
    {
        var oversizedReport = new CspViolationReport
        {
            CspReport = new CspViolationDetails
            {
                BlockedUri = "https://example.com/" + new string('x', 70000),
                ViolatedDirective = "script-src 'self'",
                OriginalPolicy = "default-src 'self'",
                DocumentUri = "https://app.example.com/page"
            }
        };

        var jsonContent = JsonSerializer.Serialize(oversizedReport, _kebabCaseJsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/csp-violation-report", content);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }
}
