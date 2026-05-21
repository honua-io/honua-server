// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;
using Honua.Server.Tests;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Simple integration tests for API key authentication focusing only on auth functionality
/// </summary>
[Collection("Database")]
public class SimpleAuthenticationTests : IAsyncLifetime, IDisposable
{
    private const string TestEncryptionMasterKey = "test-master-key-that-is-at-least-32-characters-long";
    private readonly ITestOutputHelper _output;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SimpleAuthenticationTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new TestWebApplicationFactory()
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
                        ["ConnectionStrings:honua"] = TestConnectionStrings.DefaultPostgresConnectionString
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
    public async Task DevelopmentBypass_NoPassword_ShouldRequireExplicitOptIn()
    {
        // Arrange - Development environment with no password and no explicit HONUA_DEV_AUTH
        using var factory = new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", ""); // Empty password
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString
                    });
                });
            });
        using var client = factory.CreateClient();

        // Act - Try to access admin endpoint without API key
        var response = await client.GetAsync("/api/v1/admin/connections/test/tables");

        // Assert - Should return 401 because implicit dev bypass was removed for security.
        // Auth bypass now requires explicit HONUA_DEV_AUTH=true.
        Assert.Equal(401, (int)response.StatusCode);
        _output.WriteLine($"Development bypass test - Admin response status: {response.StatusCode}");
    }

    [Fact]
    public void DevelopmentBypass_ExplicitDevAuth_ShouldBeRejectedAtStartup()
    {
        // Arrange - Development environment with HONUA_DEV_AUTH explicitly set to true
        using var factory = new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", ""); // Empty password
                builder.UseSetting("HONUA_DEV_AUTH", "true");   // Explicit opt-in
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString
                    });
                });
            });

        // Act & Assert - Development startup should fail fast when DEV_AUTH is enabled.
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Configuration validation failed", exception.Message);
    }

    [Fact]
    public void ExplicitDevBypass_Production_ShouldBeRejected()
    {
        // Arrange - Explicitly enable development bypass in production
        using var factory = new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "some-password");
                builder.UseSetting("Security:ConnectionEncryption:MasterKey", TestEncryptionMasterKey);
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:honua"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:redis"] = "localhost"
                    });
                });
            });

        // Act & Assert - Startup should fail fast in production when DEV_AUTH is enabled.
        // Accept either the generic "Configuration validation failed" wording or any
        // HONUA_DEV_AUTH-mentioning rejection from the validator, so future validator
        // wording tweaks don't break the assertion.
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.True(
            exception.Message.Contains("HONUA_DEV_AUTH", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Configuration validation failed", StringComparison.OrdinalIgnoreCase),
            $"Expected production startup rejection message to mention HONUA_DEV_AUTH or 'Configuration validation failed', got: {exception.Message}");
    }

    [Fact]
    public void MissingConnectionEncryptionMasterKey_Production_ShouldBeRejected()
    {
        // Arrange - Production startup with no encryption key configuration.
        using var factory = new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "some-password");
                builder.UseSetting("Security:ConnectionEncryption:MasterKey", string.Empty);
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:honua"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:redis"] = "localhost"
                    });
                });
            });

        // Act & Assert - Startup should fail fast when encryption key is missing.
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Configuration validation failed", exception.Message);
    }

    [Fact]
    public void InvalidConnectionEncryptionSalt_Production_ShouldBeRejected()
    {
        // Arrange - Production startup with an invalid base64 encryption salt.
        using var factory = new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "some-password");
                builder.UseSetting("Security:ConnectionEncryption:MasterKey", TestEncryptionMasterKey);
                builder.UseSetting("Security:ConnectionEncryption:Salt", "not-base64");
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:honua"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:redis"] = "localhost"
                    });
                });
            });

        // Act & Assert - Startup should fail fast when encryption salt is invalid.
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Configuration validation failed", exception.Message);
    }
}
