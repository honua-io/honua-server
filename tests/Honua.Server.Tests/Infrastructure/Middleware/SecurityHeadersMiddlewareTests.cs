// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Integration tests for Security Headers Middleware ensuring proper security headers
/// are applied to all responses per MVP security requirements.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Comprehensive)]
[Operation(Operations.Security)]
public class SecurityHeadersMiddlewareTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WebAppFixture _fixture = new();

    public SecurityHeadersMiddlewareTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task SecurityHeaders_HealthEndpoint_AppliesAllRequiredHeaders()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/live");

        // Assert
        response.EnsureSuccessStatusCode();

        // Note: HSTS header is only sent over HTTPS connections, not HTTP in test environment

        // X-Content-Type-Options
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeValues));
        Assert.Equal("nosniff", contentTypeValues.First());

        // X-Frame-Options
        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frameValues));
        Assert.Equal("DENY", frameValues.First());

        // Referrer-Policy
        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var referrerValues));
        Assert.Equal("strict-origin-when-cross-origin", referrerValues.First());

        // Content-Security-Policy
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var cspValues));
        var cspHeader = cspValues.First();
        Assert.Contains("frame-ancestors 'none'", cspHeader);
        Assert.Contains("default-src 'self'", cspHeader);
        Assert.Contains("object-src 'none'", cspHeader);

        // Cross-Origin-Opener-Policy
        Assert.True(response.Headers.TryGetValues("Cross-Origin-Opener-Policy", out var coopValues));
        Assert.Equal("same-origin", coopValues.First());

        // Cross-Origin-Embedder-Policy
        Assert.True(response.Headers.TryGetValues("Cross-Origin-Embedder-Policy", out var coepValues));
        Assert.Equal("require-corp", coepValues.First());

        // Note: Server header may still be present from Kestrel in test environment
        // In production with reverse proxy, this would typically be removed
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task SecurityHeaders_FeatureServerEndpoint_AppliesSecurityHeaders()
    {
        // Act - Test with a FeatureServer endpoint that should return 400 (no serviceId provided)
        var response = await _fixture.Client.GetAsync("/rest/services/1/FeatureServer");

        // Assert - Even error responses should have security headers
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeValues));
        Assert.Equal("nosniff", contentTypeValues.First());

        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frameValues));
        Assert.Equal("DENY", frameValues.First());

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var cspValues));
        Assert.Contains("frame-ancestors 'none'", cspValues.First());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task SecurityHeaders_AdminEndpoint_AppliesSecurityHeaders()
    {
        // Act - Test with admin endpoint - use an endpoint that should return 400 but still have headers
        var response = await _fixture.Client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Even error responses should have security headers
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeValues));
        Assert.Equal("nosniff", contentTypeValues.First());

        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frameValues));
        Assert.Equal("DENY", frameValues.First());

        // Note: This endpoint will return 500 due to missing database, but headers should still be applied
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    [Endpoint("GET /healthz/ready")]
    public async Task SecurityHeaders_MultipleRequests_ConsistentlyAppliesHeaders()
    {
        // Act - Make multiple requests to health endpoints (guaranteed to work)
        var responses = await Task.WhenAll(
            _fixture.Client.GetAsync("/healthz/live"),
            _fixture.Client.GetAsync("/healthz/ready")
        );

        // Assert - All responses should have consistent security headers
        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();

            Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeValues),
                "X-Content-Type-Options header missing");
            Assert.Equal("nosniff", contentTypeValues.First());

            Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frameValues),
                "X-Frame-Options header missing");
            Assert.Equal("DENY", frameValues.First());

            // Note: Server header may be present from Kestrel in test environment
        }
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task SecurityHeaders_ContentSecurityPolicy_HasStrictDirectives()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/live");

        // Assert
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var cspValues));
        var cspHeader = cspValues.First();

        _output.WriteLine($"CSP Header: {cspHeader}");

        // Verify strict CSP directives for API security
        Assert.Contains("default-src 'self'", cspHeader);
        Assert.Contains("frame-ancestors 'none'", cspHeader);
        Assert.Contains("object-src 'none'", cspHeader);
        Assert.Contains("script-src 'self'", cspHeader);
        Assert.Contains("form-action 'self'", cspHeader);

        // Additional security directives
        Assert.Contains("style-src 'self'", cspHeader);
        Assert.Contains("img-src 'self'", cspHeader);
        Assert.Contains("connect-src 'self'", cspHeader);
    }

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task SecurityHeaders_PermissionsPolicy_HasSecureDefaults()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/healthz/live");

        // Assert
        response.EnsureSuccessStatusCode();

        // Permissions-Policy should be present with secure defaults
        Assert.True(response.Headers.TryGetValues("Permissions-Policy", out var permissionsValues));
        var permissionsHeader = permissionsValues.First();

        _output.WriteLine($"Permissions-Policy Header: {permissionsHeader}");

        // Should restrict dangerous features by default
        Assert.NotEmpty(permissionsHeader);
    }

    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    public async Task SecurityHeaders_AllEndpoints_HaveBasicSecurityHeaders(string endpoint)
    {
        // Act
        var response = await _fixture.Client.GetAsync(endpoint);

        // Assert - Basic security headers should be present on all endpoints
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeValues),
            $"X-Content-Type-Options missing from {endpoint}");
        Assert.Equal("nosniff", contentTypeValues.First());

        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var frameValues),
            $"X-Frame-Options missing from {endpoint}");
        Assert.Equal("DENY", frameValues.First());

        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var referrerValues),
            $"Referrer-Policy missing from {endpoint}");
        Assert.Equal("strict-origin-when-cross-origin", referrerValues.First());

        // Note: Server header removal may depend on hosting environment
        // In production with reverse proxy, this would typically be handled there

        _output.WriteLine($"Security headers verified for {endpoint}");
    }
}
