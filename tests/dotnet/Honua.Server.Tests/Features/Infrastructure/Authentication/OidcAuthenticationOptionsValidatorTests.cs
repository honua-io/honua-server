// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Authentication;

/// <summary>
/// Unit tests for OidcAuthenticationOptionsValidator to ensure proper validation of OIDC configuration.
/// </summary>
public class OidcAuthenticationOptionsValidatorTests
{
    private readonly OidcAuthenticationOptionsValidator _validator = new();

    [UnitTest]
    public void Validate_ValidAzureAdConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            AzureAd = new AzureAdProviderOptions
            {
                Enabled = true,
                TenantId = "12345678-1234-1234-1234-123456789abc",
                ClientId = "87654321-4321-4321-4321-123456789def",
                Instance = "https://login.microsoftonline.com/",
                CallbackPath = "/signin-oidc-azuread",
                SignedOutCallbackPath = "/signout-callback-oidc-azuread",
                Scopes = ["openid", "profile", "email"]
            },
            ClaimsMapping = new ClaimsMappingOptions
            {
                NameClaimType = "name",
                RoleClaimType = "roles",
                EmailClaimType = "email",
                UserIdClaimType = "sub"
            },
            TokenValidation = new TokenValidationOptions
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(5)
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_ValidGoogleConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Google = new GoogleProviderOptions
            {
                Enabled = true,
                ClientId = "123456789.apps.googleusercontent.com",
                ClientSecret = "GOCSPX-abcdefghijklmnopqrstuvwxyz",
                CallbackPath = "/signin-oidc-google",
                Scopes = ["openid", "profile", "email"]
            },
            ClaimsMapping = new ClaimsMappingOptions
            {
                NameClaimType = "name",
                RoleClaimType = "roles",
                EmailClaimType = "email",
                UserIdClaimType = "sub"
            },
            TokenValidation = new TokenValidationOptions()
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_OidcEnabledWithoutProviders_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true, // Enabled but no providers configured
            DefaultRole = "user",
            AdminRoles = ["admin"]
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("At least one OIDC provider must be enabled"));
    }

    [UnitTest]
    public void Validate_EmptyDefaultRole_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            DefaultRole = "", // Invalid: empty
            AdminRoles = ["admin"]
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("DefaultRole") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_EmptyAdminRoles_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            DefaultRole = "user",
            AdminRoles = [] // Invalid: empty
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("AdminRoles") && f.Contains("contain"));
    }

    [UnitTest]
    public void Validate_HttpsNotRequiredInProduction_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = false, // Invalid in production
            DefaultRole = "user",
            AdminRoles = ["admin"],
            AzureAd = new AzureAdProviderOptions
            {
                Enabled = true,
                TenantId = "12345678-1234-1234-1234-123456789abc",
                ClientId = "87654321-4321-4321-4321-123456789def"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("RequireHttps") && f.Contains("should be true"));
    }

    [UnitTest]
    public void Validate_AzureAdMissingTenantId_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            AzureAd = new AzureAdProviderOptions
            {
                Enabled = true,
                TenantId = "", // Invalid: empty
                ClientId = "87654321-4321-4321-4321-123456789def"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("TenantId") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_AzureAdInvalidTenantId_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            AzureAd = new AzureAdProviderOptions
            {
                Enabled = true,
                TenantId = "invalid-tenant-id", // Invalid: not GUID or special value
                ClientId = "87654321-4321-4321-4321-123456789def"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("TenantId") && f.Contains("must be a valid GUID"));
    }

    [UnitTest]
    public void Validate_AzureAdSpecialTenantValues_ReturnsSuccess()
    {
        // Arrange & Act & Assert
        var validTenantIds = new[] { "common", "organizations", "consumers" };

        foreach (var tenantId in validTenantIds)
        {
            var options = new OidcAuthenticationOptions
            {
                Enabled = true,
                DefaultRole = "user",
                AdminRoles = ["admin"],
                AzureAd = new AzureAdProviderOptions
                {
                    Enabled = true,
                    TenantId = tenantId,
                    ClientId = "87654321-4321-4321-4321-123456789def"
                }
            };

            var result = _validator.Validate(null, options);

            Assert.True(result.Succeeded, $"Tenant ID '{tenantId}' should be valid");
        }
    }

    [UnitTest]
    public void Validate_GoogleMissingClientSecret_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Google = new GoogleProviderOptions
            {
                Enabled = true,
                ClientId = "123456789.apps.googleusercontent.com",
                ClientSecret = "" // Invalid: empty
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("ClientSecret") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_GenericOidcMissingAuthority_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "", // Invalid: empty
                ClientId = "my-client-id"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Authority") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_GenericOidcNonHttpsAuthority_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "http://insecure-authority.com", // Invalid: not HTTPS
                ClientId = "my-client-id"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Authority") && f.Contains("must use HTTPS"));
    }

    [UnitTest]
    public void Validate_DuplicateCallbackPaths_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            AzureAd = new AzureAdProviderOptions
            {
                Enabled = true,
                TenantId = "12345678-1234-1234-1234-123456789abc",
                ClientId = "87654321-4321-4321-4321-123456789def",
                CallbackPath = "/signin-oidc" // Duplicate path
            },
            Google = new GoogleProviderOptions
            {
                Enabled = true,
                ClientId = "123456789.apps.googleusercontent.com",
                ClientSecret = "secret",
                CallbackPath = "/signin-oidc" // Duplicate path
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Callback path") && f.Contains("used by multiple providers"));
    }

    [UnitTest]
    public void Validate_ScopesWithoutOpenId_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Google = new GoogleProviderOptions
            {
                Enabled = true,
                ClientId = "123456789.apps.googleusercontent.com",
                ClientSecret = "secret",
                Scopes = ["profile", "email"] // Missing "openid"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("must include 'openid' scope"));
    }

    [UnitTest]
    public void Validate_InvalidResponseType_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://auth.example.com",
                ClientId = "my-client-id",
                ResponseType = "invalid-response-type" // Invalid response type
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("ResponseType") && f.Contains("must be 'code'"));
    }

    [UnitTest]
    public void Validate_InvalidCallbackPath_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Google = new GoogleProviderOptions
            {
                Enabled = true,
                ClientId = "123456789.apps.googleusercontent.com",
                ClientSecret = "secret",
                CallbackPath = "invalid-path" // Invalid: doesn't start with /
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("CallbackPath") && f.Contains("must start with '/'"));
    }

    [UnitTest]
    public void Validate_ExcessiveClockSkew_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            TokenValidation = new TokenValidationOptions
            {
                ClockSkew = TimeSpan.FromMinutes(45) // Invalid: too much skew
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("TokenValidation.ClockSkew") && f.Contains("between"));
    }

    [UnitTest]
    public void Validate_EmptyClaimsMapping_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            ClaimsMapping = new ClaimsMappingOptions
            {
                NameClaimType = "", // Invalid: empty
                RoleClaimType = "roles",
                EmailClaimType = "email",
                UserIdClaimType = "sub"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("NameClaimType") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_NullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _validator.Validate(null, null!));
    }

    [UnitTest]
    public void Validate_GenericAuthorizationCodeFlowWithPkce_ReturnsSuccess()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://auth.example.com",
                ClientId = "my-client-id",
                ResponseType = "code",
                UsePkce = true
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [UnitTest]
    public void Validate_GenericAuthorizationCodeFlowWithoutPkce_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://auth.example.com",
                ClientId = "my-client-id",
                ResponseType = "code",
                UsePkce = false
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("UsePkce") && f.Contains("must be enabled"));
    }

    [UnitTest]
    public void Validate_TokenReplayProtection_ReturnsSuccess()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            TokenValidation = new TokenValidationOptions
            {
                EnableTokenReplayProtection = true,
                TokenReplayCacheDuration = TimeSpan.FromHours(2)
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    #region Okta Validation Tests

    [UnitTest]
    public void Validate_ValidOktaConfiguration_ReturnsSuccess()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Okta = new OktaProviderOptions
            {
                Enabled = true,
                OrgUrl = "dev-12345.okta.com",
                AuthorizationServerId = "default",
                ClientId = "0oa1b2c3d4e5f6g7h8i9",
                CallbackPath = "/signin-oidc-okta",
                SignedOutCallbackPath = "/signout-callback-oidc-okta",
                Scopes = ["openid", "profile", "email"]
            },
            ClaimsMapping = new ClaimsMappingOptions
            {
                NameClaimType = "name",
                RoleClaimType = "roles",
                EmailClaimType = "email",
                UserIdClaimType = "sub"
            },
            TokenValidation = new TokenValidationOptions()
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_OktaMissingOrgUrl_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Okta = new OktaProviderOptions
            {
                Enabled = true,
                OrgUrl = "",
                ClientId = "0oa1b2c3d4e5f6g7h8i9"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Okta.OrgUrl") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_OktaMissingClientId_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Okta = new OktaProviderOptions
            {
                Enabled = true,
                OrgUrl = "dev-12345.okta.com",
                ClientId = ""
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Okta.ClientId") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_OktaOrgUrlWithScheme_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Okta = new OktaProviderOptions
            {
                Enabled = true,
                OrgUrl = "https://dev-12345.okta.com",
                ClientId = "0oa1b2c3d4e5f6g7h8i9"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Okta.OrgUrl") && f.Contains("domain only"));
    }

    #endregion

    #region Auth0 Validation Tests

    [UnitTest]
    public void Validate_ValidAuth0Configuration_ReturnsSuccess()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Auth0 = new Auth0ProviderOptions
            {
                Enabled = true,
                Domain = "myapp.us.auth0.com",
                ClientId = "abc123def456ghi789",
                Audience = "https://api.myapp.com",
                RoleClaimNamespace = "https://myapp.example.com",
                CallbackPath = "/signin-oidc-auth0",
                SignedOutCallbackPath = "/signout-callback-oidc-auth0",
                Scopes = ["openid", "profile", "email"]
            },
            ClaimsMapping = new ClaimsMappingOptions
            {
                NameClaimType = "name",
                RoleClaimType = "roles",
                EmailClaimType = "email",
                UserIdClaimType = "sub"
            },
            TokenValidation = new TokenValidationOptions()
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures ?? Array.Empty<string>());
    }

    [UnitTest]
    public void Validate_Auth0MissingDomain_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Auth0 = new Auth0ProviderOptions
            {
                Enabled = true,
                Domain = "",
                ClientId = "abc123"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Auth0.Domain") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_Auth0MissingClientId_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Auth0 = new Auth0ProviderOptions
            {
                Enabled = true,
                Domain = "myapp.us.auth0.com",
                ClientId = ""
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Auth0.ClientId") && f.Contains("cannot be empty"));
    }

    [UnitTest]
    public void Validate_Auth0DomainWithScheme_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Auth0 = new Auth0ProviderOptions
            {
                Enabled = true,
                Domain = "https://myapp.us.auth0.com",
                ClientId = "abc123"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Auth0.Domain") && f.Contains("domain only"));
    }

    [UnitTest]
    public void Validate_Auth0InvalidRoleClaimNamespace_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Auth0 = new Auth0ProviderOptions
            {
                Enabled = true,
                Domain = "myapp.us.auth0.com",
                ClientId = "abc123",
                RoleClaimNamespace = "http://insecure.example.com"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Auth0.RoleClaimNamespace") && f.Contains("must use HTTPS"));
    }

    [UnitTest]
    public void Validate_DuplicateCallbackPaths_AcrossFiveProviders_ReturnsFail()
    {
        // Arrange - Okta and Auth0 share the same callback path
        var options = new OidcAuthenticationOptions
        {
            Enabled = true,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            Okta = new OktaProviderOptions
            {
                Enabled = true,
                OrgUrl = "dev-12345.okta.com",
                ClientId = "okta-client",
                CallbackPath = "/signin-oidc-shared"
            },
            Auth0 = new Auth0ProviderOptions
            {
                Enabled = true,
                Domain = "myapp.us.auth0.com",
                ClientId = "auth0-client",
                CallbackPath = "/signin-oidc-shared"
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("Callback path") && f.Contains("used by multiple providers"));
    }

    [UnitTest]
    public void Validate_AdditionalRoleClaimTypes_WithEmptyEntry_ReturnsFail()
    {
        // Arrange
        var options = new OidcAuthenticationOptions
        {
            Enabled = false,
            DefaultRole = "user",
            AdminRoles = ["admin"],
            ClaimsMapping = new ClaimsMappingOptions
            {
                NameClaimType = "name",
                RoleClaimType = "roles",
                EmailClaimType = "email",
                UserIdClaimType = "sub",
                AdditionalRoleClaimTypes = ["groups", ""]
            }
        };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("AdditionalRoleClaimTypes") && f.Contains("empty"));
    }

    #endregion

    #region PostConfigure Auto-Population Tests

    /// <summary>
    /// Symmetric signing key used to bypass OIDC metadata discovery in tests.
    /// </summary>
    private const string TestSigningKey = "super-secret-test-key-that-is-long-enough-for-hs256!";

    [UnitTest]
    public void PostConfigure_OktaRequestGroupsClaim_PopulatesAdditionalRoleClaimTypes()
    {
        // Arrange — build a minimal DI container with Configure + AddOidcAuthentication
        // to prove PostConfigure populates AdditionalRoleClaimTypes on the DI-bound instance.
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:RequireHttps"] = "false",
            ["Oidc:Okta:Enabled"] = "true",
            ["Oidc:Okta:OrgUrl"] = "dev-12345.okta.com",
            ["Oidc:Okta:ClientId"] = "test-client",
            ["Oidc:Okta:RequestGroupsClaim"] = "true",
            ["Oidc:TokenValidation:SymmetricSigningKey"] = TestSigningKey
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<OidcAuthenticationOptions>(
            configuration.GetSection(OidcAuthenticationOptions.SectionName));
        services.AddOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();

        // Act — resolve the DI-bound options
        var resolvedOptions = provider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;

        // Assert — AdditionalRoleClaimTypes should contain "groups" via PostConfigure
        Assert.Contains("groups", resolvedOptions.ClaimsMapping.AdditionalRoleClaimTypes);
    }

    [UnitTest]
    public void PostConfigure_Auth0RoleClaimNamespace_PopulatesAdditionalRoleClaimTypes()
    {
        // Arrange
        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["Oidc:Enabled"] = "true",
            ["Oidc:RequireHttps"] = "false",
            ["Oidc:Auth0:Enabled"] = "true",
            ["Oidc:Auth0:Domain"] = "myapp.us.auth0.com",
            ["Oidc:Auth0:ClientId"] = "test-client",
            ["Oidc:Auth0:RoleClaimNamespace"] = "https://myapp.example.com",
            ["Oidc:TokenValidation:SymmetricSigningKey"] = TestSigningKey
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemoryConfig)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<OidcAuthenticationOptions>(
            configuration.GetSection(OidcAuthenticationOptions.SectionName));
        services.AddOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();

        // Act
        var resolvedOptions = provider.GetRequiredService<IOptions<OidcAuthenticationOptions>>().Value;

        // Assert — namespace-prefixed roles and permissions should be populated
        Assert.Contains("https://myapp.example.com/roles", resolvedOptions.ClaimsMapping.AdditionalRoleClaimTypes);
        Assert.Contains("https://myapp.example.com/permissions", resolvedOptions.ClaimsMapping.AdditionalRoleClaimTypes);
    }

    #endregion
}
