// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>
/// Tests for ApiKeyAuthenticationHandler - critical security authentication component
/// </summary>
public sealed class ApiKeyAuthenticationHandlerTests
{
    private readonly IOptionsMonitor<AuthenticationSchemeOptions> _mockOptions;
    private readonly ILoggerFactory _mockLoggerFactory;
    private readonly ILogger<ApiKeyAuthenticationHandler> _mockLogger;
    private readonly UrlEncoder _urlEncoder;
    private readonly IConnectionSecretResolver _mockSecretResolver;
    private readonly ApiKeyAuthenticationOptions _authOptions;
    private readonly ApiKeyAuthenticationDependencies _dependencies;
    private readonly HttpContext _httpContext;

    public ApiKeyAuthenticationHandlerTests()
    {
        _mockOptions = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        _mockLoggerFactory = Substitute.For<ILoggerFactory>();
        _mockLogger = Substitute.For<ILogger<ApiKeyAuthenticationHandler>>();
        _urlEncoder = UrlEncoder.Default;
        _mockSecretResolver = Substitute.For<IConnectionSecretResolver>();

        _authOptions = new ApiKeyAuthenticationOptions
        {
            EnableDevelopmentBypass = false,
            RequireHttps = true,
            ValidApiKeys = new[] { "valid-api-key-123", "another-valid-key-456" }
        };

        _dependencies = new ApiKeyAuthenticationDependencies
        {
            Options = _authOptions,
            SecretResolver = _mockSecretResolver
        };

        _mockLoggerFactory.CreateLogger<ApiKeyAuthenticationHandler>().Returns(_mockLogger);

        // Setup HttpContext
        _httpContext = new DefaultHttpContext();
        _httpContext.Request.Scheme = "https";
        _httpContext.Request.Host = new HostString("api.example.com");
        _httpContext.Request.Path = "/api/test";

        // Setup options
        var authScheme = new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        var authOptions = new AuthenticationSchemeOptions();
        _mockOptions.Get("ApiKey").Returns(authOptions);
    }

    private ApiKeyAuthenticationHandler CreateHandler()
    {
        var handler = new ApiKeyAuthenticationHandler(
            _mockOptions,
            _mockLoggerFactory,
            _urlEncoder,
            _dependencies);

        handler.InitializeAsync(
            new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler)),
            _httpContext).GetAwaiter().GetResult();

        return handler;
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullDependencies_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new ApiKeyAuthenticationHandler(
            _mockOptions,
            _mockLoggerFactory,
            _urlEncoder,
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_ValidApiKeyInHeader_ReturnsSuccess()
    {
        // Arrange
        _httpContext.Request.Headers["X-API-Key"] = "valid-api-key-123";
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.Identity.AuthenticationType.Should().Be("ApiKey");
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_InvalidApiKey_ReturnsFailure()
    {
        // Arrange
        _httpContext.Request.Headers["X-API-Key"] = "invalid-api-key";
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
        result.Failure!.Message.Should().Contain("Invalid API key");
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_NoApiKeyProvided_ReturnsNoResult()
    {
        // Arrange
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.None.Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_EmptyApiKey_ReturnsNoResult()
    {
        // Arrange
        _httpContext.Request.Headers["X-API-Key"] = string.Empty;
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.None.Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_WhitespaceOnlyApiKey_ReturnsNoResult()
    {
        // Arrange
        _httpContext.Request.Headers["X-API-Key"] = "   ";
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.None.Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_BasicAuthWithValidCredentials_ReturnsSuccess()
    {
        // Arrange
        var credentials = "admin:valid-password";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        _httpContext.Request.Headers["Authorization"] = $"Basic {encoded}";

        // Mock environment variable or secret resolver for password validation
        Environment.SetEnvironmentVariable("HONUA_ADMIN_PASSWORD", "valid-password");

        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();

        // Cleanup
        Environment.SetEnvironmentVariable("HONUA_ADMIN_PASSWORD", null);
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_BasicAuthWithInvalidCredentials_ReturnsFailure()
    {
        // Arrange
        var credentials = "admin:wrong-password";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        _httpContext.Request.Headers["Authorization"] = $"Basic {encoded}";

        Environment.SetEnvironmentVariable("HONUA_ADMIN_PASSWORD", "correct-password");

        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();

        // Cleanup
        Environment.SetEnvironmentVariable("HONUA_ADMIN_PASSWORD", null);
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_MalformedBasicAuth_ReturnsFailure()
    {
        // Arrange
        _httpContext.Request.Headers["Authorization"] = "Basic invalid-base64!@#";
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_DevelopmentBypassEnabled_ReturnsSuccess()
    {
        // Arrange
        _authOptions.EnableDevelopmentBypass = true;
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirst(ClaimTypes.Name)!.Value.Should().Be("dev-bypass");
    }

    [Fact]
    [SecurityTest]
    public async Task HandleAuthenticateAsync_HttpConnection_WithRequireHttps_ReturnsFailure()
    {
        // Arrange
        _httpContext.Request.Scheme = "http"; // Insecure connection
        _httpContext.Request.Headers["X-API-Key"] = "valid-api-key-123";
        _authOptions.RequireHttps = true;

        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("HTTPS");
    }

    [Fact]
    [SecurityTest]
    public async Task HandleAuthenticateAsync_SqlInjectionInApiKey_HandledSecurely()
    {
        // Arrange
        var maliciousApiKey = "'; DROP TABLE users; --";
        _httpContext.Request.Headers["X-API-Key"] = maliciousApiKey;
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();
        // Should not throw or cause any side effects
    }

    [Fact]
    [SecurityTest]
    public async Task HandleAuthenticateAsync_VeryLongApiKey_HandledSecurely()
    {
        // Arrange
        var veryLongApiKey = new string('a', 10000); // 10KB key
        _httpContext.Request.Headers["X-API-Key"] = veryLongApiKey;
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();
        // Should not cause memory issues or timeouts
    }

    [Fact]
    [SecurityTest]
    public async Task HandleAuthenticateAsync_NullByteInApiKey_HandledSecurely()
    {
        // Arrange
        var maliciousApiKey = "valid-key\0injected-content";
        _httpContext.Request.Headers["X-API-Key"] = maliciousApiKey;
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_MultipleApiKeyHeaders_UsesFirst()
    {
        // Arrange
        _httpContext.Request.Headers.Add("X-API-Key", new[] { "valid-api-key-123", "second-key" });
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue(); // First key is valid
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_BothApiKeyAndBasicAuth_PrefersApiKey()
    {
        // Arrange
        _httpContext.Request.Headers["X-API-Key"] = "valid-api-key-123";
        _httpContext.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));

        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue();
        // Should use API key authentication, not Basic auth
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_SuccessfulAuthentication_SetsCorrectClaims()
    {
        // Arrange
        _httpContext.Request.Headers["X-API-Key"] = "valid-api-key-123";
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue();

        var principal = result.Principal!;
        principal.HasClaim(ClaimTypes.Name, "api-key-user").Should().BeTrue();
        principal.HasClaim(ClaimTypes.AuthenticationMethod, "api-key").Should().BeTrue();
        principal.Identity!.AuthenticationType.Should().Be("ApiKey");
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_WithSecretResolver_UsesResolvedSecrets()
    {
        // Arrange
        var secretApiKey = "secret-resolved-key";
        _mockSecretResolver.ResolveApiKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { secretApiKey });

        _httpContext.Request.Headers["X-API-Key"] = secretApiKey;
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue();
        await _mockSecretResolver.Received(1).ResolveApiKeysAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [UnitTest]
    public async Task HandleAuthenticateAsync_SecretResolverThrows_FallsBackToConfiguredKeys()
    {
        // Arrange
        _mockSecretResolver.ResolveApiKeysAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Secret resolver failed"));

        _httpContext.Request.Headers["X-API-Key"] = "valid-api-key-123"; // From configured options
        var handler = CreateHandler();

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Should().NotBeNull();
        result.Succeeded.Should().BeTrue(); // Should fallback to configured keys
    }

    [Fact]
    public async Task HandleAuthenticateAsync_HighVolumeRequests_PerformsEfficiently()
    {
        // Arrange
        const int requestCount = 1000;
        var handler = CreateHandler();
        _httpContext.Request.Headers["X-API-Key"] = "valid-api-key-123";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        for (int i = 0; i < requestCount; i++)
        {
            await handler.AuthenticateAsync();
        }

        stopwatch.Stop();

        // Assert
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000); // Should complete in under 5 seconds
        var averageTimePerRequest = stopwatch.ElapsedMilliseconds / (double)requestCount;
        averageTimePerRequest.Should().BeLessThan(5); // Should average under 5ms per request
    }

    [Fact]
    [SecurityTest]
    public async Task HandleAuthenticateAsync_TimingAttack_ConstantTime()
    {
        // Arrange
        var handler = CreateHandler();
        var validKey = "valid-api-key-123";
        var invalidKey = "invalid-key";

        // Act & Assert - Timing attack resistance test
        var validKeyTimes = new List<long>();
        var invalidKeyTimes = new List<long>();

        for (int i = 0; i < 100; i++)
        {
            // Test valid key timing
            _httpContext.Request.Headers.Clear();
            _httpContext.Request.Headers["X-API-Key"] = validKey;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await handler.AuthenticateAsync();
            sw.Stop();
            validKeyTimes.Add(sw.ElapsedTicks);

            // Test invalid key timing
            _httpContext.Request.Headers.Clear();
            _httpContext.Request.Headers["X-API-Key"] = invalidKey;
            sw.Restart();
            await handler.AuthenticateAsync();
            sw.Stop();
            invalidKeyTimes.Add(sw.ElapsedTicks);
        }

        // Timing should be similar enough to prevent timing attacks
        var avgValidTime = validKeyTimes.Average();
        var avgInvalidTime = invalidKeyTimes.Average();
        var timingRatio = Math.Max(avgValidTime, avgInvalidTime) / Math.Min(avgValidTime, avgInvalidTime);

        timingRatio.Should().BeLessThan(2.0); // Timing difference should be less than 2x
    }
}