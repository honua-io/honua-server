// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
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
            // Remove existing Admin policy if present
            var existingPolicy = authzOptions.GetPolicy(AuthenticationExtensions.AdminPolicy);
            if (existingPolicy != null)
            {
                // Recreate with OIDC support
                authzOptions.AddPolicy(AuthenticationExtensions.AdminPolicy, policy =>
                {
                    policy.RequireAuthenticatedUser();

                    // Accept admin role from any authentication scheme
                    policy.RequireAssertion(context =>
                    {
                        // Check for admin role claim
                        var hasAdminRole = context.User.IsInRole("admin") ||
                            oidcOptions.AdminRoles.Any(role => context.User.IsInRole(role));

                        return hasAdminRole;
                    });

                    // Add all authentication schemes
                    policy.AuthenticationSchemes.Add(AuthenticationExtensions.ApiKeyScheme);
                    policy.AuthenticationSchemes.Add(JwtBearerScheme);

                    if (oidcOptions.AzureAd?.IsValid == true)
                    {
                        policy.AuthenticationSchemes.Add(AzureAdScheme);
                    }

                    if (oidcOptions.Google?.IsValid == true)
                    {
                        policy.AuthenticationSchemes.Add(GoogleScheme);
                    }

                    if (oidcOptions.Generic?.IsValid == true)
                    {
                        policy.AuthenticationSchemes.Add(OidcScheme);
                    }
                });
            }
        });

        return services;
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
            var validIssuers = new List<string>();

            if (oidcOptions.AzureAd?.IsValid == true)
            {
                var azureIssuer = $"{oidcOptions.AzureAd.Instance}{oidcOptions.AzureAd.TenantId}/v2.0";
                validIssuers.Add(azureIssuer);
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
                validIssuers.AddRange(oidcOptions.TokenValidation.ValidIssuers);
            }

            options.TokenValidationParameters.ValidIssuers = validIssuers;

            // Set valid audiences
            var validAudiences = new List<string>();

            if (oidcOptions.AzureAd?.IsValid == true)
            {
                validAudiences.Add(oidcOptions.AzureAd.ClientId!);
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
                validAudiences.AddRange(oidcOptions.TokenValidation.ValidAudiences);
            }

            options.TokenValidationParameters.ValidAudiences = validAudiences;

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
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    var userId = context.Principal?.FindFirst(oidcOptions.ClaimsMapping.UserIdClaimType)?.Value ?? "unknown";
                    OidcAuthenticationLog.JwtTokenValidated(logger, userId);
                    return Task.CompletedTask;
                }
            };
        });
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
