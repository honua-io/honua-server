// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Extension methods for configuring OIDC authentication services.
/// </summary>
public static class OidcAuthenticationExtensions
{
    /// <summary>
    /// Authentication scheme name for Azure AD.
    /// </summary>
    public const string AzureAdScheme = "AzureAd";

    /// <summary>
    /// Authentication scheme name for Google.
    /// </summary>
    public const string GoogleScheme = "Google";

    /// <summary>
    /// Authentication scheme name for generic OIDC provider.
    /// </summary>
    public const string OidcScheme = "Oidc";

    /// <summary>
    /// Authentication scheme name for JWT Bearer (for API access with tokens).
    /// </summary>
    public const string JwtBearerScheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>
    /// Composite scheme name for OIDC + API key authentication.
    /// </summary>
    public const string CompositeScheme = "Composite";

    /// <summary>
    /// Adds OIDC authentication services with multi-provider support.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var oidcOptions = new OidcAuthenticationOptions();
        configuration.GetSection(OidcAuthenticationOptions.SectionName).Bind(oidcOptions);

        if (!oidcOptions.Enabled)
        {
            return services;
        }

        ResolveOidcSecrets(oidcOptions);

        // Disable automatic claim type mapping to preserve original claim names
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        var authBuilder = services.AddAuthentication(options =>
        {
            // Use a policy scheme that selects between API key and JWT Bearer
            options.DefaultScheme = CompositeScheme;
            options.DefaultChallengeScheme = CompositeScheme;
        });

        // Add JWT Bearer authentication for API access
        if (HasAnyProviderEnabled(oidcOptions))
        {
            ConfigureJwtBearerAuthentication(authBuilder, oidcOptions, configuration);
        }

        // Configure Azure AD if enabled
        if (oidcOptions.AzureAd?.IsValid == true)
        {
            ConfigureAzureAdAuthentication(authBuilder, oidcOptions.AzureAd, oidcOptions);
        }

        // Configure Google if enabled
        if (oidcOptions.Google?.IsValid == true)
        {
            ConfigureGoogleAuthentication(authBuilder, oidcOptions.Google, oidcOptions);
        }

        // Configure generic OIDC if enabled
        if (oidcOptions.Generic?.IsValid == true)
        {
            ConfigureGenericOidcAuthentication(authBuilder, oidcOptions.Generic, oidcOptions);
        }

        // Add composite policy scheme that handles both API key and JWT Bearer
        authBuilder.AddPolicyScheme(CompositeScheme, "API Key or JWT Bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                // Check for Authorization header with Bearer token
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return JwtBearerScheme;
                }

                // Fall back to API key authentication
                return AuthenticationExtensions.ApiKeyScheme;
            };
        });

        // Register claims transformation service
        services.AddScoped<IClaimsTransformation, OidcClaimsTransformation>();

        return services;
    }

    /// <summary>
    /// Updates authorization policies to include OIDC schemes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOidcAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var oidcOptions = new OidcAuthenticationOptions();
        configuration.GetSection(OidcAuthenticationOptions.SectionName).Bind(oidcOptions);

        if (!oidcOptions.Enabled)
        {
            return services;
        }

        // Update the admin policy to accept OIDC-authenticated users with admin roles
        services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(authzOptions =>
        {
            var schemes = BuildSchemes(oidcOptions);
            var adminRoles = BuildRoleSet(oidcOptions.AdminRoles, "admin", "administrator", "Administrator");

            UpdateRolePolicy(
                authzOptions,
                AuthenticationExtensions.AdminPolicy,
                adminRoles,
                schemes);

            UpdateRolePolicy(
                authzOptions,
                AuthenticationExtensions.AdminPolicyAlias,
                adminRoles,
                schemes);
        });

        return services;
    }

    private static List<string> BuildSchemes(OidcAuthenticationOptions oidcOptions)
    {
        var schemes = new List<string>
        {
            AuthenticationExtensions.ApiKeyScheme,
            JwtBearerScheme
        };

        if (oidcOptions.AzureAd?.IsValid == true)
        {
            schemes.Add(AzureAdScheme);
        }

        if (oidcOptions.Google?.IsValid == true)
        {
            schemes.Add(GoogleScheme);
        }

        if (oidcOptions.Generic?.IsValid == true)
        {
            schemes.Add(OidcScheme);
        }

        return schemes;
    }

    private static HashSet<string> BuildRoleSet(IEnumerable<string> roles, params string[] additionalRoles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in roles)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                set.Add(role.Trim());
            }
        }

        foreach (var role in additionalRoles)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                set.Add(role.Trim());
            }
        }

        return set;
    }

    private static void UpdateRolePolicy(
        Microsoft.AspNetCore.Authorization.AuthorizationOptions authzOptions,
        string policyName,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> schemes)
    {
        var existingPolicy = authzOptions.GetPolicy(policyName);
        if (existingPolicy == null)
        {
            return;
        }

        authzOptions.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => roles.Any(role => context.User.IsInRole(role)));

            foreach (var scheme in schemes)
            {
                policy.AuthenticationSchemes.Add(scheme);
            }
        });
    }

    private static bool HasAnyProviderEnabled(OidcAuthenticationOptions options)
    {
        return options.AzureAd?.IsValid == true ||
               options.Google?.IsValid == true ||
               options.Generic?.IsValid == true;
    }

    private static void ConfigureJwtBearerAuthentication(
        AuthenticationBuilder builder,
        OidcAuthenticationOptions oidcOptions,
        IConfiguration configuration)
    {
        builder.AddJwtBearer(JwtBearerScheme, options =>
        {
            // Configure token validation parameters
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = oidcOptions.TokenValidation.ValidateIssuer,
                ValidateAudience = oidcOptions.TokenValidation.ValidateAudience,
                ValidateLifetime = oidcOptions.TokenValidation.ValidateLifetime,
                ValidateIssuerSigningKey = oidcOptions.TokenValidation.ValidateIssuerSigningKey,
                ClockSkew = oidcOptions.TokenValidation.ClockSkew,
                NameClaimType = oidcOptions.ClaimsMapping.NameClaimType,
                RoleClaimType = oidcOptions.ClaimsMapping.RoleClaimType,
            };

            // Set valid issuers based on configured providers
            var validIssuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (oidcOptions.AzureAd?.IsValid == true)
            {
                var tenantId = oidcOptions.AzureAd.TenantId!;
                var azureIssuer = $"{oidcOptions.AzureAd.Instance}{tenantId}/v2.0";
                validIssuers.Add(azureIssuer);
                validIssuers.Add($"https://sts.windows.net/{tenantId}/");
            }

            if (oidcOptions.Google?.IsValid == true)
            {
                validIssuers.Add("https://accounts.google.com");
            }

            if (oidcOptions.Generic?.IsValid == true && !string.IsNullOrEmpty(oidcOptions.Generic.Authority))
            {
                validIssuers.Add(oidcOptions.Generic.Authority);
            }

            if (oidcOptions.TokenValidation.ValidIssuers.Length > 0)
            {
                foreach (var issuer in oidcOptions.TokenValidation.ValidIssuers)
                {
                    validIssuers.Add(issuer);
                }
            }

            options.TokenValidationParameters.ValidIssuers = validIssuers.ToArray();

            // Set valid audiences
            var validAudiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (oidcOptions.AzureAd?.IsValid == true)
            {
                var clientId = oidcOptions.AzureAd.ClientId!;
                validAudiences.Add(clientId);
                validAudiences.Add($"api://{clientId}");
            }

            if (oidcOptions.Google?.IsValid == true)
            {
                validAudiences.Add(oidcOptions.Google.ClientId!);
            }

            if (oidcOptions.Generic?.IsValid == true)
            {
                validAudiences.Add(oidcOptions.Generic.ClientId!);
            }

            if (oidcOptions.TokenValidation.ValidAudiences.Length > 0)
            {
                foreach (var audience in oidcOptions.TokenValidation.ValidAudiences)
                {
                    validAudiences.Add(audience);
                }
            }

            options.TokenValidationParameters.ValidAudiences = validAudiences.ToArray();

            // Configure authority for metadata retrieval
            // Use the first available provider's authority
            if (oidcOptions.AzureAd?.IsValid == true)
            {
                options.Authority = $"{oidcOptions.AzureAd.Instance}{oidcOptions.AzureAd.TenantId}/v2.0";
            }
            else if (oidcOptions.Generic?.IsValid == true)
            {
                options.Authority = oidcOptions.Generic.Authority;
            }

            // For multiple issuers, we need to disable automatic issuer validation
            // and handle it via ValidIssuers instead
            if (validIssuers.Count > 1)
            {
                options.TokenValidationParameters.ValidateIssuer = true;
            }

            options.RequireHttpsMetadata = oidcOptions.RequireHttps;

            // Event handlers for logging
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.JwtAuthenticationFailed(logger, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    var userId = context.Principal?.FindFirst(oidcOptions.ClaimsMapping.UserIdClaimType)?.Value ?? "unknown";
                    OidcAuthenticationLog.JwtTokenValidated(logger, userId);

                    if (oidcOptions.TokenValidation.EnableTokenReplayProtection)
                    {
                        var distributedCache = context.HttpContext.RequestServices.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
                        var memoryCache = context.HttpContext.RequestServices.GetService<IMemoryCache>();
                        var tokenKey = TryGetTokenReplayKey(context.SecurityToken);
                        if (!string.IsNullOrWhiteSpace(tokenKey) && context.SecurityToken is JwtSecurityToken jwtToken)
                        {
                            var expiresOn = GetReplayCacheExpiration(jwtToken, oidcOptions.TokenValidation);

                            // Prefer distributed cache for multi-instance deployments
                            if (distributedCache != null)
                            {
                                var existing = await distributedCache.GetStringAsync(tokenKey);
                                if (existing != null)
                                {
                                    OidcAuthenticationLog.TokenReplayDetected(logger, userId);
                                    context.Fail("Token replay detected");
                                    return;
                                }

                                await distributedCache.SetStringAsync(tokenKey, "1",
                                    new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                                    {
                                        AbsoluteExpiration = expiresOn
                                    });
                            }
                            else if (memoryCache != null)
                            {
                                // Fall back to in-memory cache for single-instance deployments
                                if (memoryCache.TryGetValue(tokenKey, out _))
                                {
                                    OidcAuthenticationLog.TokenReplayDetected(logger, userId);
                                    context.Fail("Token replay detected");
                                    return;
                                }

                                memoryCache.Set(tokenKey, true, new MemoryCacheEntryOptions
                                {
                                    AbsoluteExpiration = expiresOn
                                });
                            }
                        }
                    }
                }
            };
        });
    }

    private static void ResolveOidcSecrets(OidcAuthenticationOptions options)
    {
        options.AzureAd?.ClientSecret = ResolveSecretReference(options.AzureAd?.ClientSecret, "Oidc:AzureAd:ClientSecret");
        options.Google?.ClientSecret = ResolveSecretReference(options.Google?.ClientSecret, "Oidc:Google:ClientSecret");
        options.Generic?.ClientSecret = ResolveSecretReference(options.Generic?.ClientSecret, "Oidc:Generic:ClientSecret");
    }

    private static string? ResolveSecretReference(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var envVar = value[4..].Trim();
        if (string.IsNullOrWhiteSpace(envVar))
        {
            throw new InvalidOperationException($"Invalid secret reference for {settingName}. Expected env:VARIABLE_NAME.");
        }

        var resolved = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException($"Environment variable '{envVar}' is not set for {settingName}.");
        }

        return resolved;
    }

    private static string? TryGetTokenReplayKey(SecurityToken token)
    {
        if (token is JwtSecurityToken jwtToken)
        {
            if (!string.IsNullOrWhiteSpace(jwtToken.Id))
            {
                return $"jti:{jwtToken.Id}";
            }

            if (!string.IsNullOrWhiteSpace(jwtToken.RawData))
            {
                return $"raw:{jwtToken.RawData}";
            }
        }

        return null;
    }

    private static DateTime GetReplayCacheExpiration(JwtSecurityToken token, TokenValidationOptions options)
    {
        var now = DateTime.UtcNow;
        var expires = token.ValidTo == DateTime.MinValue
            ? now
            : token.ValidTo.ToUniversalTime();

        if (options.TokenReplayCacheDuration > TimeSpan.Zero)
        {
            var maxExpiration = now.Add(options.TokenReplayCacheDuration);
            if (expires == DateTime.MinValue || expires > maxExpiration)
            {
                expires = maxExpiration;
            }
        }

        if (expires <= now)
        {
            expires = now.AddMinutes(5);
        }

        return expires;
    }

    private static void ConfigureAzureAdAuthentication(
        AuthenticationBuilder builder,
        AzureAdProviderOptions azureAdOptions,
        OidcAuthenticationOptions oidcOptions)
    {
        builder.AddOpenIdConnect(AzureAdScheme, "Azure AD", options =>
        {
            options.Authority = $"{azureAdOptions.Instance}{azureAdOptions.TenantId}/v2.0";
            options.ClientId = azureAdOptions.ClientId;
            options.ClientSecret = azureAdOptions.ClientSecret;
            options.CallbackPath = azureAdOptions.CallbackPath;
            options.SignedOutCallbackPath = azureAdOptions.SignedOutCallbackPath;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.RequireHttpsMetadata = oidcOptions.RequireHttps;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = oidcOptions.ClaimsMapping.NameClaimType,
                RoleClaimType = oidcOptions.ClaimsMapping.RoleClaimType,
            };

            foreach (var scope in azureAdOptions.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.Events = new OpenIdConnectEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcAuthenticationFailed(logger, AzureAdScheme, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
                    OidcAuthenticationLog.OidcTokenValidated(logger, AzureAdScheme, userId);
                    return Task.CompletedTask;
                }
            };
        });
    }

    private static void ConfigureGoogleAuthentication(
        AuthenticationBuilder builder,
        GoogleProviderOptions googleOptions,
        OidcAuthenticationOptions oidcOptions)
    {
        builder.AddOpenIdConnect(GoogleScheme, "Google", options =>
        {
            options.Authority = "https://accounts.google.com";
            options.ClientId = googleOptions.ClientId;
            options.ClientSecret = googleOptions.ClientSecret;
            options.CallbackPath = googleOptions.CallbackPath;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.RequireHttpsMetadata = oidcOptions.RequireHttps;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = oidcOptions.ClaimsMapping.NameClaimType,
                RoleClaimType = oidcOptions.ClaimsMapping.RoleClaimType,
            };

            foreach (var scope in googleOptions.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.Events = new OpenIdConnectEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcAuthenticationFailed(logger, GoogleScheme, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
                    OidcAuthenticationLog.OidcTokenValidated(logger, GoogleScheme, userId);
                    return Task.CompletedTask;
                }
            };
        });
    }

    private static void ConfigureGenericOidcAuthentication(
        AuthenticationBuilder builder,
        GenericOidcProviderOptions genericOptions,
        OidcAuthenticationOptions oidcOptions)
    {
        builder.AddOpenIdConnect(OidcScheme, genericOptions.DisplayName, options =>
        {
            options.Authority = genericOptions.Authority;
            options.ClientId = genericOptions.ClientId;
            options.ClientSecret = genericOptions.ClientSecret;
            options.CallbackPath = genericOptions.CallbackPath;
            options.SignedOutCallbackPath = genericOptions.SignedOutCallbackPath;
            options.ResponseType = genericOptions.ResponseType;
            options.UsePkce = genericOptions.UsePkce;
            options.SaveTokens = genericOptions.SaveTokens;
            options.GetClaimsFromUserInfoEndpoint = genericOptions.GetClaimsFromUserInfoEndpoint;
            options.RequireHttpsMetadata = oidcOptions.RequireHttps;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = oidcOptions.ClaimsMapping.NameClaimType,
                RoleClaimType = oidcOptions.ClaimsMapping.RoleClaimType,
            };

            foreach (var scope in genericOptions.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.Events = new OpenIdConnectEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcAuthenticationFailed(logger, OidcScheme, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
                    OidcAuthenticationLog.OidcTokenValidated(logger, OidcScheme, userId);
                    return Task.CompletedTask;
                }
            };
        });
    }
}
