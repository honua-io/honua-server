// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Tests;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Integration tests for OIDC authentication with JWT Bearer support.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin, Protocols.Health, Protocols.FeatureServer)]
[Operation(Operations.Security)]
public class OidcAuthenticationTests : IAsyncLifetime
{
    private const string TestConnectionEncryptionMasterKey = "AuthTestsConnectionMasterKey-123456";
    private const string TestAdminPassword = "TestAdminPassword123!";
    private readonly ITestOutputHelper _output;
    private readonly WebAppFixture _fixture = new();
    private const string TestSigningKey = "ThisIsATestSigningKeyThatIsLongEnoughForHS256Algorithm!";
    private const string TestIssuer = "https://test-issuer.example.com";
    private const string TestAudience = "test-client-id";

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
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                configure?.Invoke(builder);

                if (oidcSettings != null)
                {
                    foreach (var setting in oidcSettings)
                    {
                        builder.UseSetting(setting.Key, setting.Value);
                    }
                }

                if (string.IsNullOrWhiteSpace(builder.GetSetting(WebHostDefaults.EnvironmentKey)))
                {
                    builder.UseEnvironment("Test");
                }

                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestPostgresConnectionString,
                        ["ConnectionStrings:honua"] = TestPostgresConnectionString,
                        ["Security:ConnectionEncryption:MasterKey"] = TestConnectionEncryptionMasterKey
                    };

                    if (oidcSettings != null)
                    {
                        foreach (var setting in oidcSettings)
                        {
                            settings[setting.Key] = setting.Value;
                        }
                    }

                    var configuredAdminPassword = builder.GetSetting("HONUA_ADMIN_PASSWORD");
                    if (!string.IsNullOrWhiteSpace(configuredAdminPassword))
                    {
                        settings["HONUA_ADMIN_PASSWORD"] = configuredAdminPassword;
                    }

                    var devAuth = builder.GetSetting("HONUA_DEV_AUTH");
                    if (!string.IsNullOrWhiteSpace(devAuth))
                    {
                        settings["HONUA_DEV_AUTH"] = devAuth;
                    }

                    var basicCompat = builder.GetSetting("HONUA_ENABLE_BASIC_AUTH_COMPAT");
                    if (!string.IsNullOrWhiteSpace(basicCompat))
                    {
                        settings["HONUA_ENABLE_BASIC_AUTH_COMPAT"] = basicCompat;
                    }

                    var requireHttps = builder.GetSetting("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH");
                    if (!string.IsNullOrWhiteSpace(requireHttps))
                    {
                        settings["HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH"] = requireHttps;
                    }

                    configBuilder.AddInMemoryCollection(settings);
                });

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDatabaseCompatibilityChecker>();
                    services.AddSingleton<IDatabaseCompatibilityChecker, AlwaysCompatibleDatabaseCompatibilityChecker>();

                    // Remove the real PostgreSQL services
                    services.RemoveAll<Npgsql.NpgsqlDataSource>();
                    services.RemoveAll<IDatabaseConnectionProvider>();
                    services.AddSingleton(_ => NpgsqlDataSource.Create(TestPostgresConnectionString));

                    // Add mock implementations
                    services.AddScoped<ILayerCatalog>(provider => new TestLayerCatalog());
                    services.AddScoped<TestFeatureStore>();
                    services.AddScoped<IFeatureReader>(provider => provider.GetRequiredService<TestFeatureStore>());
                    services.AddScoped<IFeatureWriter>(provider => provider.GetRequiredService<TestFeatureStore>());
                    services.AddScoped<ITileProvider>(provider => provider.GetRequiredService<TestFeatureStore>());
                    services.AddScoped<IRelationshipStore>(provider => provider.GetRequiredService<TestFeatureStore>());
                    services.AddScoped<IStreamingFeatureStore>(provider => provider.GetRequiredService<TestFeatureStore>());
                });
            });
    }

    private static string TestPostgresConnectionString => TestConnectionStrings.DefaultPostgresConnectionString;

    private sealed class AlwaysCompatibleDatabaseCompatibilityChecker : IDatabaseCompatibilityChecker
    {
        public Task<DatabaseCompatibilityResult> CheckCompatibilityAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseCompatibilityResult.Compatible(
                "PostgreSQL test",
                "3.6.2",
                "3.6.2",
                ["postgis", "postgis_raster"]));
    }

    private static Dictionary<string, string?> CreateEnabledOidcSettings(Dictionary<string, string?>? additionalSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:Generic:Enabled"] = "true",
            ["Oidc:Generic:Authority"] = TestIssuer,
            ["Oidc:Generic:ClientId"] = TestAudience,
            ["Oidc:ClaimsMapping:RoleClaimType"] = ClaimTypes.Role,
            ["Oidc:TokenValidation:SymmetricSigningKey"] = TestSigningKey,
            ["Oidc:TokenValidation:EnableTokenReplayProtection"] = "false",
            ["HONUA_ADMIN_PASSWORD"] = TestAdminPassword
        };

        if (additionalSettings != null)
        {
            foreach (var pair in additionalSettings)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        return settings;
    }

    /// <summary>
    /// Generates a test JWT token with the specified claims.
    /// </summary>
    private static string GenerateTestJwtToken(
        string userId = "test-user-id",
        string name = "Test User",
        string email = "test@example.com",
        string[]? roles = null,
        string issuer = TestIssuer,
        string audience = TestAudience,
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
                claims.Add(new Claim(ClaimTypes.Role, role));
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

    #region Okta Options Tests

    [UnitTest]
    public void OktaOptions_ValidConfiguration_IsValidReturnsTrue()
    {
        var options = new OktaProviderOptions
        {
            Enabled = true,
            OrgUrl = "dev-12345.okta.com",
            ClientId = "0oa1b2c3d4e5f6g7h8i9"
        };

        Assert.True(options.IsValid);
    }

    [UnitTest]
    public void OktaOptions_MissingOrgUrl_IsValidReturnsFalse()
    {
        var options = new OktaProviderOptions
        {
            Enabled = true,
            OrgUrl = null,
            ClientId = "0oa1b2c3d4e5f6g7h8i9"
        };

        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void OktaOptions_Disabled_IsValidReturnsFalse()
    {
        var options = new OktaProviderOptions
        {
            Enabled = false,
            OrgUrl = "dev-12345.okta.com",
            ClientId = "0oa1b2c3d4e5f6g7h8i9"
        };

        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void OktaOptions_GetAuthority_DefaultAuthServer_ReturnsCorrectUrl()
    {
        var options = new OktaProviderOptions
        {
            OrgUrl = "dev-12345.okta.com",
            AuthorizationServerId = "default"
        };

        Assert.Equal("https://dev-12345.okta.com/oauth2/default", options.GetAuthority());
    }

    [UnitTest]
    public void OktaOptions_GetAuthority_CustomAuthServer_ReturnsCorrectUrl()
    {
        var options = new OktaProviderOptions
        {
            OrgUrl = "myorg.okta.com",
            AuthorizationServerId = "aus1b2c3d4e5f6g7h"
        };

        Assert.Equal("https://myorg.okta.com/oauth2/aus1b2c3d4e5f6g7h", options.GetAuthority());
    }

    #endregion

    #region Auth0 Options Tests

    [UnitTest]
    public void Auth0Options_ValidConfiguration_IsValidReturnsTrue()
    {
        var options = new Auth0ProviderOptions
        {
            Enabled = true,
            Domain = "myapp.us.auth0.com",
            ClientId = "abc123def456"
        };

        Assert.True(options.IsValid);
    }

    [UnitTest]
    public void Auth0Options_MissingDomain_IsValidReturnsFalse()
    {
        var options = new Auth0ProviderOptions
        {
            Enabled = true,
            Domain = null,
            ClientId = "abc123def456"
        };

        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void Auth0Options_Disabled_IsValidReturnsFalse()
    {
        var options = new Auth0ProviderOptions
        {
            Enabled = false,
            Domain = "myapp.us.auth0.com",
            ClientId = "abc123def456"
        };

        Assert.False(options.IsValid);
    }

    [UnitTest]
    public void Auth0Options_GetAuthority_ReturnsUrlWithTrailingSlash()
    {
        var options = new Auth0ProviderOptions
        {
            Domain = "myapp.us.auth0.com"
        };

        Assert.Equal("https://myapp.us.auth0.com/", options.GetAuthority());
    }

    #endregion

    #endregion

    #region OIDC Disabled Tests

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcDisabled_ApiKeyStillWorks()
    {
        // Arrange - OIDC disabled, API key enabled
        const string adminPassword = TestAdminPassword;
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
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
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
                builder.UseSetting("HONUA_ADMIN_PASSWORD", TestAdminPassword);
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

    #region OIDC Enabled Bearer Integration Tests

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_ValidBearerToken_AllowsAccess()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"]);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        if ((int)response.StatusCode == 401)
        {
            _output.WriteLine($"401 response body: {await response.Content.ReadAsStringAsync()}");
            if (response.Headers.TryGetValues("WWW-Authenticate", out var headerValues))
            {
                _output.WriteLine($"WWW-Authenticate: {string.Join(" | ", headerValues)}");
            }
        }

        // Assert - request should pass authz and fail later (for example with 404/500) in this harness
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/resources")]
    public async Task AdminEndpoint_OidcEnabled_ReusedBearerToken_IsRejectedWhenReplayProtectionEnabled()
    {
        var settings = CreateEnabledOidcSettings(new Dictionary<string, string?>
        {
            ["Oidc:TokenValidation:EnableTokenReplayProtection"] = "true"
        });
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"]);

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/metadata/resources");
        firstRequest.Headers.Add("Authorization", $"Bearer {token}");
        var firstResponse = await client.SendAsync(firstRequest);

        var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/metadata/resources");
        secondRequest.Headers.Add("Authorization", $"Bearer {token}");
        var secondResponse = await client.SendAsync(secondRequest);

        Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
        AssertBearerFailureStatusCode(secondResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/resources")]
    public async Task AdminEndpoint_OidcEnabled_DistributedCacheWithoutRedis_FallsBackToMemoryReplayProtection()
    {
        var settings = CreateEnabledOidcSettings(new Dictionary<string, string?>
        {
            ["Oidc:TokenValidation:EnableTokenReplayProtection"] = "true"
        });
        using var factory = CreateOidcTestFactory(
            configure: builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDistributedCache>();
                    services.AddDistributedMemoryCache();
                });
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"]);

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/metadata/resources");
        firstRequest.Headers.Add("Authorization", $"Bearer {token}");
        var firstResponse = await client.SendAsync(firstRequest);

        var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/metadata/resources");
        secondRequest.Headers.Add("Authorization", $"Bearer {token}");
        var secondResponse = await client.SendAsync(secondRequest);

        Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
        AssertBearerFailureStatusCode(secondResponse.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_InvalidIssuerToken_DeniesAccess()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"], issuer: "https://wrong-issuer.example.com");

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert
        AssertBearerFailureStatusCode(response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_InvalidAudienceToken_DeniesAccess()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"], audience: "wrong-audience");

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert
        AssertBearerFailureStatusCode(response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_ExpiredToken_DeniesAccess()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"], expiresInMinutes: -60);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert
        AssertBearerFailureStatusCode(response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_NonAdminRole_ReturnsForbidden()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["viewer"]);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert - auth succeeds, authorization fails
        Assert.Equal(403, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_BearerAndApiKey_BearerTakesPrecedence_WhenInvalid()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var invalidBearer = GenerateTestJwtToken(roles: ["admin"], issuer: "https://wrong-issuer.example.com");

        // Act - valid API key is present, but invalid bearer should still fail due bearer precedence
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", TestAdminPassword);
        request.Headers.Add("Authorization", $"Bearer {invalidBearer}");
        var response = await client.SendAsync(request);

        // Assert
        AssertBearerFailureStatusCode(response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OidcEnabled_BearerAndApiKey_BearerTakesPrecedence_WhenValid()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var validBearer = GenerateTestJwtToken(roles: ["admin"]);

        // Act - invalid API key is present, valid bearer should still succeed
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("X-API-Key", "wrong-password");
        request.Headers.Add("Authorization", $"Bearer {validBearer}");
        var response = await client.SendAsync(request);

        if ((int)response.StatusCode == 401)
        {
            _output.WriteLine($"401 response body: {await response.Content.ReadAsStringAsync()}");
            if (response.Headers.TryGetValues("WWW-Authenticate", out var headerValues))
            {
                _output.WriteLine($"WWW-Authenticate: {string.Join(" | ", headerValues)}");
            }
        }

        // Assert
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task AdminWriteEndpoint_ApiKeyAuth_AllowsAuthorizationFlow()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/connections");
        request.Headers.Add("X-API-Key", TestAdminPassword);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);

        // Assert - write endpoint auth passes; downstream validation may still reject payload
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task AdminWriteEndpoint_BearerAuth_AllowsAuthorizationFlow()
    {
        // Arrange
        var settings = CreateEnabledOidcSettings();
        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"]);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/connections");
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);

        if ((int)response.StatusCode == 401)
        {
            _output.WriteLine($"401 response body: {await response.Content.ReadAsStringAsync()}");
            if (response.Headers.TryGetValues("WWW-Authenticate", out var headerValues))
            {
                _output.WriteLine($"WWW-Authenticate: {string.Join(" | ", headerValues)}");
            }
        }

        // Assert - write endpoint auth passes; downstream validation may still reject payload
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
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

    [UnitTest]
    public async Task ClaimsTransformation_OktaGroupsClaim_MappedToRoles_WhenConfigured()
    {
        // Arrange - AdditionalRoleClaimTypes includes "groups" (auto-populated when Okta.RequestGroupsClaim=true)
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["admin"],
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = ["groups"]
            }
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "okta-user-1"),
            new("groups", "engineering"),
            new("groups", "admin")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("engineering"));
        Assert.True(result.IsInRole("admin"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_OktaGroupsClaim_NotMapped_WhenNotConfigured()
    {
        // Arrange - AdditionalRoleClaimTypes is empty (RequestGroupsClaim=false)
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["admin"],
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = []
            }
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "okta-user-2"),
            new("groups", "engineering")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert - groups should NOT be mapped to roles
        Assert.False(result.IsInRole("engineering"));
        Assert.True(result.IsInRole("user")); // default role assigned instead
    }

    [UnitTest]
    public async Task ClaimsTransformation_Auth0NamespacePrefixedRoles_MappedCorrectly()
    {
        // Arrange - Auth0 namespace-prefixed roles
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["admin"],
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = ["https://myapp.example.com/roles", "https://myapp.example.com/permissions"]
            }
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "auth0|user-1"),
            new("https://myapp.example.com/roles", "admin"),
            new("https://myapp.example.com/roles", "editor"),
            new("https://myapp.example.com/permissions", "read:data")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("admin"));
        Assert.True(result.IsInRole("editor"));
        Assert.True(result.IsInRole("read:data"));
    }

    [UnitTest]
    public async Task ClaimsTransformation_Auth0PermissionsClaim_MappedToRoles()
    {
        // Arrange
        var oidcOptions = Options.Create(new OidcAuthenticationOptions
        {
            DefaultRole = "user",
            AdminRoles = ["admin"],
            ClaimsMapping = new ClaimsMappingOptions
            {
                AdditionalRoleClaimTypes = ["https://myapp.example.com/permissions"]
            }
        });

        var logger = new TestLogger<OidcClaimsTransformation>();
        var transformation = new OidcClaimsTransformation(oidcOptions, logger);

        var claims = new List<Claim>
        {
            new("sub", "auth0|user-2"),
            new("https://myapp.example.com/permissions", "write:features"),
            new("https://myapp.example.com/permissions", "delete:features")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        // Act
        var result = await transformation.TransformAsync(principal);

        // Assert
        Assert.True(result.IsInRole("write:features"));
        Assert.True(result.IsInRole("delete:features"));
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
    [Endpoint("GET /healthz/live")]
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
                builder.UseSetting("HONUA_ADMIN_PASSWORD", TestAdminPassword);
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();

        // Act - Access public health endpoint
        var response = await client.GetAsync("/healthz/live");

        // Assert - Should always be accessible
        Assert.Equal(200, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServerEndpoint_OidcEnabled_RequiresAuthByDefault()
    {
        // Arrange - OIDC enabled with default public read behavior
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
                builder.UseSetting("HONUA_ADMIN_PASSWORD", TestAdminPassword);
            },
            oidcSettings: settings);
        using var client = factory.CreateClient();

        // Act - Access FeatureServer endpoint without auth
        var response = await client.GetAsync("/rest/services/test/FeatureServer");

        // Assert - Should require authentication by default
        Assert.Equal(401, (int)response.StatusCode);
        _output.WriteLine($"FeatureServer response requires auth: {response.StatusCode}");
    }

    #endregion

    #region Okta/Auth0 JWT Bearer Integration Tests

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_OktaShapedToken_CorrectIssuerFormat_AllowsAccess()
    {
        // Arrange - Okta issuer format: https://{orgUrl}/oauth2/{authServerId}
        const string oktaIssuer = "https://dev-12345.okta.com/oauth2/default";
        var settings = CreateEnabledOidcSettings(new Dictionary<string, string?>
        {
            // Disable generic provider and use Okta instead
            ["Oidc:Generic:Enabled"] = "false",
            ["Oidc:Okta:Enabled"] = "true",
            ["Oidc:Okta:OrgUrl"] = "dev-12345.okta.com",
            ["Oidc:Okta:AuthorizationServerId"] = "default",
            ["Oidc:Okta:ClientId"] = TestAudience
        });
        // Override issuer for token validation
        settings["Oidc:TokenValidation:ValidIssuers:0"] = oktaIssuer;

        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"], issuer: oktaIssuer);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_Auth0ShapedToken_IssuerWithTrailingSlash_AllowsAccess()
    {
        // Arrange - Auth0 issuer format: https://{domain}/ (trailing slash)
        const string auth0Issuer = "https://myapp.us.auth0.com/";
        var settings = CreateEnabledOidcSettings(new Dictionary<string, string?>
        {
            // Disable generic provider and use Auth0 instead
            ["Oidc:Generic:Enabled"] = "false",
            ["Oidc:Auth0:Enabled"] = "true",
            ["Oidc:Auth0:Domain"] = "myapp.us.auth0.com",
            ["Oidc:Auth0:ClientId"] = TestAudience
        });
        settings["Oidc:TokenValidation:ValidIssuers:0"] = auth0Issuer;

        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        var token = GenerateTestJwtToken(roles: ["admin"], issuer: auth0Issuer);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task AdminEndpoint_Auth0TokenWithAudience_DistinctFromClientId_AllowsAccess()
    {
        // Arrange - Auth0 uses separate API audience
        const string auth0Issuer = "https://myapp.us.auth0.com/";
        const string apiAudience = "https://api.myapp.com";
        var settings = CreateEnabledOidcSettings(new Dictionary<string, string?>
        {
            ["Oidc:Generic:Enabled"] = "false",
            ["Oidc:Auth0:Enabled"] = "true",
            ["Oidc:Auth0:Domain"] = "myapp.us.auth0.com",
            ["Oidc:Auth0:ClientId"] = "auth0-client-id",
            ["Oidc:Auth0:Audience"] = apiAudience
        });
        settings["Oidc:TokenValidation:ValidIssuers:0"] = auth0Issuer;

        using var factory = CreateOidcTestFactory(oidcSettings: settings);
        using var client = factory.CreateClient();
        // Token uses the API audience (not the ClientId) as the audience claim
        var token = GenerateTestJwtToken(roles: ["admin"], issuer: auth0Issuer, audience: apiAudience);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/connections/test/tables");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await client.SendAsync(request);

        // Assert
        Assert.NotEqual(401, (int)response.StatusCode);
        Assert.NotEqual(403, (int)response.StatusCode);
    }

    #endregion

    private static void AssertBearerFailureStatusCode(System.Net.HttpStatusCode statusCode)
    {
        Assert.True(
            statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest,
            $"Expected Bearer failure status 400/401 but received {(int)statusCode}.");
    }

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
