// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Authentication;
using Honua.TestKit.Attributes;

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
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("AdminRoles") && f.Contains("must contain at least 1 item"));
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
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("ResponseType") && f.Contains("not a valid OIDC response type"));
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
        Assert.Contains(result.Failures ?? Array.Empty<string>(), f => f.Contains("TokenValidation.ClockSkew") && f.Contains("must be between 0 seconds and 30 minutes"));
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
    public void Validate_ValidResponseTypes_ReturnsSuccess()
    {
        // Arrange
        var validResponseTypes = new[] { "code", "id_token", "token", "id_token token", "code id_token", "code token", "code id_token token" };

        foreach (var responseType in validResponseTypes)
        {
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
                    ResponseType = responseType
                }
            };

            // Act
            var result = _validator.Validate(null, options);

            // Assert
            Assert.True(result.Succeeded, $"Response type '{responseType}' should be valid");
        }
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
}
