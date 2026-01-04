// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Simple integration tests for API key authentication focusing only on auth functionality
/// </summary>
[Collection("Database")]
public class SimpleAuthenticationTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SimpleAuthenticationTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Configure test-specific settings
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
                builder.UseSetting("HONUA_DEV_AUTH", "false"); // Disable dev bypass to test real auth

                // Configure test environment to bypass Aspire configuration (must be last)
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

    [Fact]
    public async Task HealthEndpoint_NoAuth_ShouldBeAccessible()
    {
        // Act - Access public health endpoint without API key
        var response = await _client.GetAsync("/healthz/live");

        // Assert - Should always be accessible
        Assert.Equal(200, (int)response.StatusCode);
        _output.WriteLine($"Health endpoint accessible without auth: {response.StatusCode}");
    }

    [Fact]
    public async Task DevelopmentBypass_NoPassword_ShouldAllowAccess()
    {
        // Arrange - Development environment with no password
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", ""); // Empty password
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test;Password=test"
                    });
                });
            });
        using var client = factory.CreateClient();

        // Act - Try to access admin endpoint without API key
        var response = await client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Should not return 401 when dev bypass is active
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Development bypass test - Admin response status: {response.StatusCode}");
    }

    [Fact]
    public async Task ExplicitDevBypass_Production_ShouldBeRejected()
    {
        // Arrange - Explicitly enable development bypass in production
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "some-password");
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test;Password=test",
                        ["ConnectionStrings:honua"] = "Host=localhost;Database=test;Username=test;Password=test",
                        ["ConnectionStrings:redis"] = "localhost"
                    });
                });
            });
        using var client = factory.CreateClient();

        // Act - Access admin endpoint without API key
        var response = await client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Dev bypass should be ignored in production
        Assert.Equal(401, (int)response.StatusCode);
        _output.WriteLine($"Explicit dev bypass in production test - Response status: {response.StatusCode}");
    }
}
