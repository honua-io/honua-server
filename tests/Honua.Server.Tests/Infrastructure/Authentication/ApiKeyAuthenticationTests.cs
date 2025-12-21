// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Integration tests for API key authentication with development bypass functionality
/// </summary>
[Collection("Database")]
public class ApiKeyAuthenticationTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiKeyAuthenticationTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    #region Development Bypass Tests

    [IntegrationTest]
    public async Task AdminEndpoint_DevelopmentEnvironment_NoPassword_AllowsAccess()
    {
        // Arrange - In development environment with no HONUA_ADMIN_PASSWORD set
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", ""); // Empty password
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key
        var response = await client.GetAsync("/api/admin/connections/test/tables");

        // Assert - Should allow access (will get 500 due to missing DB, but not 401)
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    [IntegrationTest]
    public async Task AdminEndpoint_DevelopmentBypass_ExplicitlyEnabled_AllowsAccess()
    {
        // Arrange - Explicitly enable development bypass
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production"); // Even in production
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "some-password");
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key
        var response = await client.GetAsync("/api/admin/connections/test/tables");

        // Assert - Should allow access due to explicit bypass
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    [IntegrationTest]
    public async Task AdminEndpoint_ProductionEnvironment_NoBypass_RequiresAuth()
    {
        // Arrange - Production environment without bypass
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key
        var response = await client.GetAsync("/api/admin/connections/test/tables");

        // Assert - Should require authentication
        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    #endregion

    #region API Key Authentication Tests

    [IntegrationTest]
    public async Task AdminEndpoint_ValidApiKey_AllowsAccess()
    {
        // Arrange
        const string adminPassword = "test-admin-password-123";
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with valid API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", adminPassword);
        var response = await client.SendAsync(request);

        // Assert - Should allow access (will get 500 due to missing DB, but not 401)
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    [IntegrationTest]
    public async Task AdminEndpoint_InvalidApiKey_DeniesAccess()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "correct-password");
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with invalid API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "wrong-password");
        var response = await client.SendAsync(request);

        // Assert - Should deny access
        Assert.Equal(401, (int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("API key required", content); // Authentication challenges return generic message
        _output.WriteLine($"Response: {content}");
    }

    [IntegrationTest]
    public async Task AdminEndpoint_EmptyApiKey_DeniesAccess()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with empty API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "");
        var response = await client.SendAsync(request);

        // Assert - Should deny access
        Assert.Equal(401, (int)response.StatusCode);
    }

    [IntegrationTest]
    public async Task AdminEndpoint_NoApiKeyHeader_DeniesAccess()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key header
        var response = await client.GetAsync("/api/admin/connections/test/tables");

        // Assert - Should deny access
        Assert.Equal(401, (int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("API key required", content);
    }

    [IntegrationTest]
    public async Task AdminEndpoint_NoAdminPassword_DeniesAccess()
    {
        // Arrange - Production environment with no admin password configured
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                // Don't set HONUA_ADMIN_PASSWORD
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with any API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "any-key");
        var response = await client.SendAsync(request);

        // Assert - Should deny access due to no admin password configured
        Assert.Equal(401, (int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Admin authentication not configured", content);
    }

    #endregion

    #region Public Endpoint Tests

    [IntegrationTest]
    public async Task HealthEndpoint_NoAuth_AlwaysAccessible()
    {
        // Arrange - Production environment with auth required
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
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
    public async Task FeatureServerEndpoint_NoAuth_AlwaysAccessible()
    {
        // Arrange - Production environment with auth required and mocked services
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
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
                    services.AddScoped<IFeatureStore>(provider => new TestFeatureStore());
                });
            });
        using var client = factory.CreateClient();

        // Act - Access public FeatureServer endpoint without API key
        var response = await client.GetAsync("/rest/services/1/FeatureServer");

        // Assert - Should always be accessible (will get error due to missing data, but not 401)
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"FeatureServer endpoint accessible without auth: {response.StatusCode}");
    }

    #endregion

    #region Edge Cases

    [IntegrationTest]
    public async Task AdminEndpoint_CaseSensitiveApiKey_DeniesAccess()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "TestPassword");
            });
        using var client = factory.CreateClient();

        // Act - Use wrong case for API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "testpassword"); // lowercase
        var response = await client.SendAsync(request);

        // Assert - Should deny access (API keys are case-sensitive)
        Assert.Equal(401, (int)response.StatusCode);
    }

    [IntegrationTest]
    public async Task AdminEndpoint_SpecialCharactersInApiKey_WorksCorrectly()
    {
        // Arrange
        const string complexPassword = "Test@123!#$%^&*()_+-=[]{}|;:,.<>?";
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", complexPassword);
            });
        using var client = factory.CreateClient();

        // Act - Use complex password with special characters
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", complexPassword);
        var response = await client.SendAsync(request);

        // Assert - Should allow access
        Assert.NotEqual(401, (int)response.StatusCode);
    }

    #endregion
}
