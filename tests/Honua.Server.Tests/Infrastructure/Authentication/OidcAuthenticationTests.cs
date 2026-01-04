// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Integration tests for OIDC authentication with JWT Bearer support.
/// </summary>
[Collection("Database")]
public class OidcAuthenticationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly WebAppFixture _fixture = new();
    private const string TestSigningKey = "ThisIsATestSigningKeyThatIsLongEnoughForHS256Algorithm!";

    public OidcAuthenticationTests(ITestOutputHelper output)
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
    /// Helper method to configure WebApplicationFactory with OIDC settings.
    /// </summary>
    private static WebApplicationFactory<Program> CreateOidcTestFactory(
        Action<IWebHostBuilder>? configure = null,
        Dictionary<string, string?>? oidcSettings = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                configure?.Invoke(builder);
                builder.UseEnvironment("Test");

                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:honua"] = "Host=localhost;Database=test;Username=test;Password=test"
                    };

                    if (oidcSettings != null)
                    {
                        foreach (var setting in oidcSettings)
                        {
                            settings[setting.Key] = setting.Value;
                        }
                    }

                    configBuilder.AddInMemoryCollection(settings);
                });

                builder.ConfigureTestServices(services =>
                {
                    // Remove the real PostgreSQL services
                    services.RemoveAll<Npgsql.NpgsqlDataSource>();
                    services.RemoveAll<IDatabaseConnectionProvider>();

                    // Add mock implementations
                    services.AddScoped<ILayerCatalog>(provider => new TestLayerCatalog());
                    services.AddScoped<IFeatureStore>(provider => new TestFeatureStore());
                });
            });
    }

    /// <summary>
    /// Generates a test JWT token with the specified claims.
    /// </summary>
    private static string GenerateTestJwtToken(
        string userId = "test-user-id",
        string name = "Test User",
        string email = "test@example.com",
        string[]? roles = null,
        string issuer = "https://test-issuer.example.com",
        string audience = "test-client-id",
        int expiresInMinutes = 60)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", userId),
            new("name", name),
            new("email", email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (roles != null)
        {
            foreach (var role in roles)
            {
                claims.Add(new Claim("roles", role));
            }
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiresInMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    #region OIDC Configuration Tests

    [UnitTest]
    public void OidcOptions_DefaultConfiguration_HasExpectedDefaults()
    {
        // Arrange
        var options = new OidcAuthenticationOptions();

        // Assert - verify default values
        Assert.False(options.Enabled);
        Assert.True(options.RequireHttps);
        Assert.Equal("user", options.DefaultRole);
        Assert.Contains("admin", options.AdminRoles);
        Assert.Contains("administrator", options.AdminRoles);
    }

    [UnitTest]
    public void AzureAdOptions_ValidConfiguration_IsValidReturnsTrue()
    {
        // Arrange
        var options = new AzureAdProviderOptions
        {
            Enabled = true,
            TenantId = "test-tenant-id",
            ClientId = "test-client-id",
            ClientSecret = "test-secret"
        };

        // Assert
        Assert.True(options.IsValid);
    }

    [UnitTest]
    public void AzureAdOptions_MissingTenantId_IsValidReturnsFalse()
    {
        // Arrange
        var options = new AzureAdProviderOptions
        {
            Enabled = true,
            TenantId = null,
            ClientId = "test-client-id"
        };

        // Assert
        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void AzureAdOptions_MissingClientId_IsValidReturnsFalse()
    {
        // Arrange
        var options = new AzureAdProviderOptions
        {
            Enabled = true,
            TenantId = "test-tenant-id",
            ClientId = null
        };

        // Assert
        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void AzureAdOptions_Disabled_IsValidReturnsFalse()
    {
        // Arrange
        var options = new AzureAdProviderOptions
        {
            Enabled = false,
            TenantId = "test-tenant-id",
            ClientId = "test-client-id"
        };

        // Assert
        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void GoogleOptions_ValidConfiguration_IsValidReturnsTrue()
    {
        // Arrange
        var options = new GoogleProviderOptions
        {
            Enabled = true,
            ClientId = "test-client-id",
            ClientSecret = "test-secret"
        };

        // Assert
        Assert.True(options.IsValid);
    }

    [UnitTest]
    public void GoogleOptions_MissingClientSecret_IsValidReturnsFalse()
    {
        // Arrange
        var options = new GoogleProviderOptions
        {
            Enabled = true,
            ClientId = "test-client-id",
            ClientSecret = null
        };

        // Assert
        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void GenericOidcOptions_ValidConfiguration_IsValidReturnsTrue()
    {
        // Arrange
        var options = new GenericOidcProviderOptions
        {
            Enabled = true,
            Authority = "https://identity.example.com",
            ClientId = "test-client-id"
        };

        // Assert
        Assert.True(options.IsValid);
    }

    [UnitTest]
    public void GenericOidcOptions_MissingAuthority_IsValidReturnsFalse()
    {
        // Arrange
        var options = new GenericOidcProviderOptions
        {
            Enabled = true,
            Authority = null,
            ClientId = "test-client-id"
        };

        // Assert
        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void GenericOidcOptions_EmptyAuthority_IsValidReturnsFalse()
    {
        // Arrange
        var options = new GenericOidcProviderOptions
        {
            Enabled = true,
            Authority = "",
            ClientId = "test-client-id"
        };

        // Assert
        Assert.False(options.IsValid);
    }

    #endregion

    #region OIDC Disabled Tests

    [IntegrationTest]
    public async Task AdminEndpoint_OidcDisabled_ApiKeyStillWorks()
    {
        // Arrange - OIDC disabled, API key enabled
        const string adminPassword = "test-admin-password";
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "false",
            ["HONUA_ADMIN_PASSWORD"] = adminPassword
        };

        using var factory = CreateOidcTestFactory(
            configure: builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", adminPassword);
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();

        // Act - Access admin endpoint with API key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", adminPassword);
        var response = await client.SendAsync(request);

        // Assert - Should allow access
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    [IntegrationTest]
    public async Task AdminEndpoint_OidcDisabled_BearerTokenIgnored()
    {
        // Arrange - OIDC disabled
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "false"
        };

        using var factory = CreateOidcTestFactory(
            configure: builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();

        // Generate a valid-looking JWT token
        var token = GenerateTestJwtToken();

        // Act - Access admin endpoint with Bearer token
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert - Should deny access (OIDC is disabled)
        Assert.Equal(401, (int)response.StatusCode);
        _output.WriteLine($"Response status: {response.StatusCode}");
    }

    #endregion

    #region Claims Transformation Tests

    [UnitTest]
    public void ClaimsMapping_DefaultConfiguration_HasExpectedDefaults()
    {
        // Arrange
        var options = new ClaimsMappingOptions();

        // Assert
        Assert.Equal("name", options.NameClaimType);
        Assert.Equal("roles", options.RoleClaimType);
        Assert.Equal("email", options.EmailClaimType);
        Assert.Equal("sub", options.UserIdClaimType);
        Assert.Empty(options.CustomMappings);
    }

    [UnitTest]
    public async Task ClaimsTransformation_WithValidClaims_TransformsCorrectly()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["admin", "superuser"]
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "user-123"),
            new("name", "Test User"),
            new("email", "test@example.com"),
            new("roles", "admin")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("admin"));
        Assert.NotNull(result.FindFirst(ClaimTypes.NameIdentifier));
        Assert.Equal("user-123", result.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [UnitTest]
    public async Task ClaimsTransformation_WithoutRoles_AddsDefaultRole()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "readonly-user",
            AdminRoles = ["admin"]
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "user-456"),
            new("name", "Regular User")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("readonly-user"));
        Assert.False(result.IsInRole("admin"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_WithAdminRole_GrantsAdminAccess()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["superadmin", "platform-admin"]
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "admin-789"),
            new("name", "Admin User"),
            new("roles", "platform-admin")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("admin"));
        Assert.True(result.IsInRole("platform-admin"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_ApiKeyAuth_SkipsTransformation()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "oidc-user"
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "admin"),
            new(ClaimTypes.Role, "admin"),
            new("auth_type", "admin")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert - should not add default OIDC role
        Assert.False(result.IsInRole("oidc-user"));
        Assert.True(result.IsInRole("admin"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_WithCustomMappings_AppliesCustomClaims()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            ClaimsMapping = new ClaimsMappingOptions
            {
                CustomMappings = new Dictionary<string, string>
                {
                    ["department"] = "user_department",
                    ["employee_id"] = "user_employee_id"
                }
            }
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "user-custom"),
            new("department", "Engineering"),
            new("employee_id", "EMP-12345")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.Equal("Engineering", result.FindFirst("user_department")?.Value);
        Assert.Equal("EMP-12345", result.FindFirst("user_employee_id")?.Value);
    }

    [UnitTest]
    public async Task ClaimsTransformation_WithMultipleRoles_PreservesAllRoles()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["admin"]
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "user-multi"),
            new("roles", "reader"),
            new("roles", "writer"),
            new("roles", "reviewer")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("reader"));
        Assert.True(result.IsInRole("writer"));
        Assert.True(result.IsInRole("reviewer"));
        Assert.False(result.IsInRole("admin"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_UnauthenticatedPrincipal_ReturnsUnchanged()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user"
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var identity = new ClaimsIdentity(); // Not authenticated (no auth type)
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert - should return unchanged
        Assert.False(result.Identity?.IsAuthenticated);
        Assert.False(result.IsInRole("user"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_NormalizesEmailFromUpn()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user"
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        // Azure AD often provides email in UPN claim
        var claims = new List<Claim>
        {
            new("sub", "user-upn"),
            new("upn", "user@contoso.com")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert - email should be extracted from UPN
        Assert.Equal("user@contoso.com", result.FindFirst(ClaimTypes.Email)?.Value);
    }

    [UnitTest]
    public async Task ClaimsTransformation_NormalizesNameFromPreferredUsername()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user"
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        // Some providers use preferred_username instead of name
        var claims = new List<Claim>
        {
            new("sub", "user-preferred"),
            new("preferred_username", "jdoe")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert - name should be extracted from preferred_username
        Assert.Equal("jdoe", result.FindFirst(ClaimTypes.Name)?.Value);
    }

    #endregion

    #region Token Validation Options Tests

    [UnitTest]
    public void TokenValidationOptions_DefaultConfiguration_IsSecure()
    {
        // Arrange
        var options = new TokenValidationOptions();

        // Assert - secure defaults
        Assert.True(options.ValidateIssuer);
        Assert.True(options.ValidateAudience);
        Assert.True(options.ValidateLifetime);
        Assert.True(options.ValidateIssuerSigningKey);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ClockSkew);
    }

    #endregion

    #region Public Endpoints with OIDC Tests

    [IntegrationTest]
    public async Task HealthEndpoint_OidcEnabled_StillAccessible()
    {
        // Arrange - OIDC enabled but health should still work
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = "https://test.example.com",
            ["Oidc:Generic:ClientId"] = "test-client"
        };

        using var factory = CreateOidcTestFactory(
            configure: builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();

        // Act - Access public health endpoint
        var response = await client.GetAsync("/healthz/live");

        // Assert - Should always be accessible
        Assert.Equal(200, (int)response.StatusCode);
    }

    [IntegrationTest]
    public async Task FeatureServerEndpoint_OidcEnabled_StillAccessible()
    {
        // Arrange - OIDC enabled but FeatureServer should still work without auth
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = "https://test.example.com",
            ["Oidc:Generic:ClientId"] = "test-client"
        };

        using var factory = CreateOidcTestFactory(
            configure: builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "test-password");
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();

        // Act - Access FeatureServer endpoint without auth
        var response = await client.GetAsync("/rest/services/1/FeatureServer");

        // Assert - Should not return 401
        Assert.NotEqual(401, (int)response.StatusCode);
        _output.WriteLine($"FeatureServer response: {response.StatusCode}");
    }

    #endregion

    /// <summary>
    /// Test logger implementation for unit testing.
    /// </summary>
    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        { }
    }
}
