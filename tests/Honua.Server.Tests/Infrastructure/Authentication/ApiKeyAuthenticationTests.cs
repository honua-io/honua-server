// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Integration tests for API key authentication with development bypass functionality
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin, Protocols.Health, Protocols.FeatureServer)]
[Operation(Operations.Security)]
public class ApiKeyAuthenticationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WebAppFixture _fixture = new();

    public ApiKeyAuthenticationTests(ITestOutputHelper output)
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

    /// <summary>
    /// Helper method to configure WebApplicationFactory with proper test environment settings.
    /// </summary>
    private static WebApplicationFactory<Program> CreateTestFactory(Action<IWebHostBuilder> configure)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                // Apply additional test-specific configuration first
                configure?.Invoke(builder);

                // Override with test environment to bypass Aspire configuration (must be last)
                builder.UseEnvironment("Test");

                // Configure test connection string to avoid connection string errors (must be last)
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:honua"] = "Host=localhost;Database=test;Username=test;Password=test"
                    });
                });
            });
    }

    #region Development Bypass Tests

    // Note: AdminEndpoint_DevelopmentEnvironment_NoPassword_AllowsAccess test removed
    // API keys are now required in development environment

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_DevelopmentBypass_ExplicitlyEnabled_AllowsAccess()
    {
        // Arrange - Explicitly enable development bypass
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "true");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "some-password");
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key
        var response = await client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Should allow access due to explicit bypass
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_ProductionEnvironment_NoBypass_RequiresAuth()
    {
        // Arrange - Production environment without bypass
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key
        var response = await client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Should require authentication
        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    #endregion

    #region API Key Authentication Tests

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_ValidApiKey_AllowsAccess()
    {
        // Arrange
        const string adminPassword = "test-admin-password-123";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with valid API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", adminPassword);
        var response = await client.SendAsync(request);

        // Assert - Should allow access (will get 500 due to missing DB, but not 401)
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_InvalidApiKey_DeniesAccess()
    {
        // Arrange
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "correct-password");
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with invalid API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "wrong-password");
        var response = await client.SendAsync(request);

        // Assert - Should deny access
        Assert.Equal(401, (int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("API key required", content); // Authentication challenges return generic message
        _output.WriteLine($"Response: {content}");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_EmptyApiKey_DeniesAccess()
    {
        // Arrange
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with empty API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "");
        var response = await client.SendAsync(request);

        // Assert - Should deny access
        Assert.Equal(401, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_NoApiKeyHeader_DeniesAccess()
    {
        // Arrange
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key header
        var response = await client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Should deny access
        Assert.Equal(401, (int)response.StatusCode);
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out var challengeHeaders));
        Assert.Contains(challengeHeaders, value => value.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("API key required", content);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_NoAdminPassword_DeniesAccess()
    {
        // Arrange - Production environment with no admin password configured
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            // Don't set HONUA_ADMIN_PASSWORD
        });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with any API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "any-key");
        var response = await client.SendAsync(request);

        // Assert - Should deny access due to no admin password configured
        Assert.Equal(401, (int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Admin authentication not configured", content);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_BasicAuthCompatibilityDisabled_DeniesAccess()
    {
        // Arrange
        const string adminPassword = "test-basic-password";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            builder.UseSetting("HONUA_ENABLE_BASIC_AUTH_COMPAT", "false");
        });
        using var client = factory.CreateClient();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{adminPassword}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(401, (int)response.StatusCode);
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out var challengeHeaders));
        Assert.DoesNotContain(challengeHeaders, value => value.Contains("Basic", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_BasicAuthCompatibilityEnabled_InsecureTransport_DeniesAccess()
    {
        // Arrange
        const string adminPassword = "test-basic-password";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            builder.UseSetting("HONUA_ENABLE_BASIC_AUTH_COMPAT", "true");
            builder.UseSetting("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", "true");
        });
        using var client = factory.CreateClient();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{adminPassword}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(401, (int)response.StatusCode);
        Assert.True(response.Headers.TryGetValues("WWW-Authenticate", out var challengeHeaders));
        Assert.Contains(challengeHeaders, value => value.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(challengeHeaders, value => value.Contains("Basic", StringComparison.OrdinalIgnoreCase));
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("requires HTTPS", content);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_BasicAuthCompatibilityEnabled_ForwardedProtoSpoof_DeniesAccess()
    {
        // Arrange
        const string adminPassword = "test-basic-password";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            builder.UseSetting("HONUA_ENABLE_BASIC_AUTH_COMPAT", "true");
            builder.UseSetting("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", "true");
        });
        using var client = factory.CreateClient();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{adminPassword}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(401, (int)response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("requires HTTPS", content);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_BasicAndApiKeyHeaders_ApiKeyHeaderTakesPrecedence()
    {
        // Arrange
        const string adminPassword = "test-basic-priority";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            builder.UseSetting("HONUA_ENABLE_BASIC_AUTH_COMPAT", "true");
            builder.UseSetting("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", "false");
        });
        using var client = factory.CreateClient();

        // Act - Use invalid Basic password but valid X-API-Key header
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", adminPassword);
        var basicEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong-password"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basicEncoded);
        var response = await client.SendAsync(request);

        // Assert - X-API-Key should win and authenticate
        Assert.NotEqual(401, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_BasicAuthCompatibilityEnabled_RequireHttpsDisabled_AllowsAccess()
    {
        // Arrange
        const string adminPassword = "test-basic-password";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            builder.UseSetting("HONUA_ENABLE_BASIC_AUTH_COMPAT", "true");
            builder.UseSetting("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", "false");
        });
        using var client = factory.CreateClient();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{adminPassword}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
        var response = await client.SendAsync(request);

        // Assert
        Assert.NotEqual(401, (int)response.StatusCode);
    }

    #endregion

    #region Public Endpoint Tests

    [IntegrationTest]
    [Endpoint("GET /healthz/live")]
    public async Task HealthEndpoint_NoAuth_AlwaysAccessible()
    {
        // Arrange - Production environment with auth required
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
        });
        using var client = factory.CreateClient();

        // Act - Access public health endpoint without API key
        var response = await client.GetAsync("/healthz/live");

        // Assert - Should always be accessible
        Assert.Equal(200, (int)response.StatusCode);
        _output.WriteLine($"Health endpoint accessible without auth: {response.StatusCode}");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServerEndpoint_NoAuth_RequiresAuthByDefault()
    {
        // Arrange - Production environment with auth required and mocked services
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");

            builder.ConfigureTestServices(services =>
            {
                // Remove the real PostgreSQL services
                services.RemoveAll<Npgsql.NpgsqlDataSource>();
                services.RemoveAll<IDatabaseConnectionProvider>();

                // Add mock implementations for services needed by FeatureServer
                services.AddScoped<ILayerCatalog>(provider => new TestLayerCatalog());
                services.AddScoped<TestFeatureStore>();
                services.AddScoped<IFeatureReader>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<IFeatureWriter>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<ITileProvider>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<IRelationshipStore>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<IStreamingFeatureStore>(provider => provider.GetRequiredService<TestFeatureStore>());
            });
        });
        using var client = factory.CreateClient();

        // Act - Access public FeatureServer endpoint without API key
        var response = await client.GetAsync("/rest/services/test/FeatureServer");

        // Assert - Should require authentication by default
        Assert.Equal(401, (int)response.StatusCode);
        _output.WriteLine($"FeatureServer endpoint requires auth by default: {response.StatusCode}");
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServerEndpoint_NoAuth_AllowPublicRead_AllowsAccess()
    {
        // Arrange - Production environment with allow-public enabled and mocked services
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");

            builder.ConfigureTestServices(services =>
            {
                // Remove the real PostgreSQL services
                services.RemoveAll<Npgsql.NpgsqlDataSource>();
                services.RemoveAll<IDatabaseConnectionProvider>();

                // Add mock implementations for services needed by FeatureServer
                services.AddScoped<ILayerCatalog>(_ => new TestLayerCatalog(
                    servicePolicy: new AccessPolicy { AllowAnonymous = true }));
                services.AddScoped<TestFeatureStore>();
                services.AddScoped<IFeatureReader>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<IFeatureWriter>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<ITileProvider>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<IRelationshipStore>(provider => provider.GetRequiredService<TestFeatureStore>());
                services.AddScoped<IStreamingFeatureStore>(provider => provider.GetRequiredService<TestFeatureStore>());
            });
        });
        using var client = factory.CreateClient();

        // Act - Access public FeatureServer endpoint without API key
        var response = await client.GetAsync("/rest/services/test/FeatureServer");

        // Assert - Should allow access when public read is enabled
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"FeatureServer endpoint accessible when public read is enabled: {response.StatusCode}");
    }

    #endregion

    #region Edge Cases

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_CaseSensitiveApiKey_DeniesAccess()
    {
        // Arrange
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", "TestPassword");
        });
        using var client = factory.CreateClient();

        // Act - Use wrong case for API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "testpassword"); // lowercase
        var response = await client.SendAsync(request);

        // Assert - Should deny access (API keys are case-sensitive)
        Assert.Equal(401, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_SpecialCharactersInApiKey_WorksCorrectly()
    {
        // Arrange
        const string complexPassword = "Test@123!#$%^&*()_+-=[]{}|;:,.<>?";
        using var factory = CreateTestFactory(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", complexPassword);
        });
        using var client = factory.CreateClient();

        // Act - Use complex password with special characters
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", complexPassword);
        var response = await client.SendAsync(request);

        // Assert - Should allow access
        Assert.NotEqual(401, (int)response.StatusCode);
    }

    #endregion
}
