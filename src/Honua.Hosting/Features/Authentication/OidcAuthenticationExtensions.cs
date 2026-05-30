// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Honua.Infrastructure.Authentication.ClientCertificates;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Extension methods for configuring OIDC authentication services.
/// </summary>
public static class OidcAuthenticationExtensions
{
    private static readonly ConcurrentDictionary<string, ReplayLockState> TokenReplayLocks = new(StringComparer.Ordinal);

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
    /// Authentication scheme name for Okta.
    /// </summary>
    public const string OktaScheme = "Okta";

    /// <summary>
    /// Authentication scheme name for Auth0.
    /// </summary>
    public const string Auth0Scheme = "Auth0";

    /// <summary>
    /// Authentication scheme name for JWT Bearer (for API access with tokens).
    /// </summary>
    public const string JwtBearerScheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>
    /// Composite scheme name for OIDC + API key authentication.
    /// </summary>
    public const string CompositeScheme = "Composite";

    /// <summary>
    /// Authentication scheme name for the server-managed admin session cookie.
    /// </summary>
    public const string AdminSessionScheme = "AdminSession";

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

        authBuilder.AddScheme<AuthenticationSchemeOptions, AdminAuthSessionAuthenticationHandler>(
            AdminSessionScheme,
            static _ => { });

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

        // Configure Okta if enabled
        if (oidcOptions.Okta?.IsValid == true)
        {
            ConfigureOktaAuthentication(authBuilder, oidcOptions.Okta, oidcOptions);
        }

        // Configure Auth0 if enabled
        if (oidcOptions.Auth0?.IsValid == true)
        {
            ConfigureAuth0Authentication(authBuilder, oidcOptions.Auth0, oidcOptions);
        }

        // Auto-populate AdditionalRoleClaimTypes from provider config on the DI-bound instance
        services.PostConfigure<OidcAuthenticationOptions>(PopulateAdditionalRoleClaimTypes);

        // Add composite policy scheme that handles both API key and JWT Bearer
        authBuilder.AddPolicyScheme(CompositeScheme, "API Key or JWT Bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                // Check for Authorization header with Bearer token
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    var currentOptions = context.RequestServices
                        .GetRequiredService<IOptions<OidcAuthenticationOptions>>()
                        .Value;
                    if (currentOptions.Enabled && HasAnyProviderEnabled(currentOptions))
                    {
                        return JwtBearerScheme;
                    }
                }

                if (context.Request.Cookies.ContainsKey(AdminAuthSessionStore.AuthSessionCookieName))
                {
                    return AdminSessionScheme;
                }

                var clientCertificateOptions = context.RequestServices
                    .GetService<IOptions<ClientCertificateAuthenticationOptions>>()?
                    .Value;
                if (clientCertificateOptions?.Mode != ClientCertificateAuthenticationMode.Disabled &&
                    (context.Connection.ClientCertificate is not null ||
                     (clientCertificateOptions?.ForwardedCertificate.Enabled == true &&
                      context.Request.Headers.ContainsKey(clientCertificateOptions.ForwardedCertificate.HeaderName))))
                {
                    return ClientCertificateAuthenticationDefaults.AuthenticationScheme;
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

        // Update the admin policy to accept composite OIDC/JWT/session and
        // client-certificate principals with admin roles.
        services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(authzOptions =>
        {
            var schemes = BuildSchemes();
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

    private static List<string> BuildSchemes()
    {
        // Admin APIs authenticate through the composite scheme. Interactive provider
        // schemes are used by backend-assisted login endpoints; adding them to API
        // authorization policies makes failed bearer-token requests perform OIDC
        // metadata discovery during challenge handling.
        var schemes = new List<string>
        {
            CompositeScheme,
            ClientCertificateAuthenticationDefaults.AuthenticationScheme
        };

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
               options.Generic?.IsValid == true ||
               options.Okta?.IsValid == true ||
               options.Auth0?.IsValid == true;
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

            if (oidcOptions.Okta?.IsValid == true)
            {
                validIssuers.Add(oidcOptions.Okta.GetAuthority());
            }

            if (oidcOptions.Auth0?.IsValid == true)
            {
                validIssuers.Add(oidcOptions.Auth0.GetAuthority());
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

            if (oidcOptions.Okta?.IsValid == true)
            {
                validAudiences.Add(oidcOptions.Okta.ClientId!);
            }

            if (oidcOptions.Auth0?.IsValid == true)
            {
                validAudiences.Add(oidcOptions.Auth0.ClientId!);
                if (!string.IsNullOrWhiteSpace(oidcOptions.Auth0.Audience))
                {
                    validAudiences.Add(oidcOptions.Auth0.Audience);
                }
            }

            if (oidcOptions.TokenValidation.ValidAudiences.Length > 0)
            {
                foreach (var audience in oidcOptions.TokenValidation.ValidAudiences)
                {
                    validAudiences.Add(audience);
                }
            }

            options.TokenValidationParameters.ValidAudiences = validAudiences.ToArray();

            var staticSigningKey = oidcOptions.TokenValidation.SymmetricSigningKey;
            if (!string.IsNullOrWhiteSpace(staticSigningKey))
            {
                options.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(staticSigningKey));
            }
            else
            {
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
                else if (oidcOptions.Okta?.IsValid == true)
                {
                    options.Authority = oidcOptions.Okta.GetAuthority();
                }
                else if (oidcOptions.Auth0?.IsValid == true)
                {
                    options.Authority = oidcOptions.Auth0.GetAuthority();
                }
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
                    OidcAuthenticationLog.JwtTokenValidated(logger);

                    if (oidcOptions.TokenValidation.EnableTokenReplayProtection &&
                        !context.HttpContext.IsAdminAuthSessionBridged())
                    {
                        var redis = context.HttpContext.RequestServices.GetService<IConnectionMultiplexer>();
                        var memoryCache = context.HttpContext.RequestServices.GetService<IMemoryCache>();
                        var tokenKey = TryGetTokenReplayKey(context.SecurityToken);
                        if (!string.IsNullOrWhiteSpace(tokenKey))
                        {
                            var expiresOn = GetReplayCacheExpiration(context.SecurityToken, oidcOptions.TokenValidation);
                            var registrationResult = await TryRegisterTokenReplayAsync(
                                tokenKey,
                                expiresOn,
                                redis,
                                memoryCache,
                                logger,
                                context.HttpContext.RequestAborted).ConfigureAwait(false);

                            if (registrationResult == TokenReplayRegistrationResult.ReplayDetected)
                            {
                                OidcAuthenticationLog.TokenReplayDetected(logger);
                                context.Fail("Token replay detected");
                                return;
                            }
                        }
                    }
                }
            };
        });
    }

    private static async Task<TokenReplayRegistrationResult> TryRegisterTokenReplayAsync(
        string tokenKey,
        DateTime expiresOn,
        IConnectionMultiplexer? redis,
        IMemoryCache? memoryCache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var expiresIn = expiresOn - DateTime.UtcNow;
        if (expiresIn <= TimeSpan.Zero)
        {
            expiresIn = TimeSpan.FromMinutes(5);
        }

        if (redis is not null)
        {
            try
            {
                if (redis.IsConnected)
                {
                    var registered = await redis.GetDatabase().StringSetAsync(
                        tokenKey,
                        "1",
                        expiresIn,
                        when: When.NotExists).ConfigureAwait(false);

                    return registered
                        ? TokenReplayRegistrationResult.Registered
                        : TokenReplayRegistrationResult.ReplayDetected;
                }

                OidcAuthenticationLog.TokenReplayRedisDisconnected(logger);
            }
            catch (RedisException ex)
            {
                OidcAuthenticationLog.TokenReplayRedisAccessFailed(logger, ex);
            }
        }

        if (memoryCache is not null)
        {
            return await TryRegisterTokenReplayInMemoryAsync(
                tokenKey,
                expiresOn,
                memoryCache,
                cancellationToken).ConfigureAwait(false);
        }

        OidcAuthenticationLog.TokenReplayCacheUnavailable(logger);
        return TokenReplayRegistrationResult.Skipped;
    }

    private static async Task<TokenReplayRegistrationResult> TryRegisterTokenReplayInMemoryAsync(
        string tokenKey,
        DateTime expiresOn,
        IMemoryCache memoryCache,
        CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue(tokenKey, out _))
        {
            return TokenReplayRegistrationResult.ReplayDetected;
        }

        var replayLock = TokenReplayLocks.GetOrAdd(tokenKey, static _ => new ReplayLockState());
        Interlocked.Increment(ref replayLock.ReferenceCount);

        try
        {
            await replayLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (memoryCache.TryGetValue(tokenKey, out _))
                {
                    return TokenReplayRegistrationResult.ReplayDetected;
                }

                memoryCache.Set(tokenKey, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = new DateTimeOffset(expiresOn)
                });

                return TokenReplayRegistrationResult.Registered;
            }
            finally
            {
                replayLock.Semaphore.Release();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref replayLock.ReferenceCount) == 0 &&
                TokenReplayLocks.TryGetValue(tokenKey, out var currentLock) &&
                ReferenceEquals(currentLock, replayLock) &&
                TokenReplayLocks.TryRemove(tokenKey, out var removedLock) &&
                ReferenceEquals(removedLock, replayLock))
            {
                replayLock.Semaphore.Dispose();
            }
        }
    }

    private static void ResolveOidcSecrets(OidcAuthenticationOptions options)
    {
        options.AzureAd?.ClientSecret = ResolveSecretReference(options.AzureAd?.ClientSecret, "Oidc:AzureAd:ClientSecret");
        options.Google?.ClientSecret = ResolveSecretReference(options.Google?.ClientSecret, "Oidc:Google:ClientSecret");
        options.Generic?.ClientSecret = ResolveSecretReference(options.Generic?.ClientSecret, "Oidc:Generic:ClientSecret");
        options.Okta?.ClientSecret = ResolveSecretReference(options.Okta?.ClientSecret, "Oidc:Okta:ClientSecret");
        options.Auth0?.ClientSecret = ResolveSecretReference(options.Auth0?.ClientSecret, "Oidc:Auth0:ClientSecret");
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
                var hash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(jwtToken.RawData));
                return $"raw:{Convert.ToHexStringLower(hash)}";
            }
        }
        else if (token is JsonWebToken jsonWebToken)
        {
            if (!string.IsNullOrWhiteSpace(jsonWebToken.Id))
            {
                return $"jti:{jsonWebToken.Id}";
            }

            if (!string.IsNullOrWhiteSpace(jsonWebToken.EncodedToken))
            {
                var hash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(jsonWebToken.EncodedToken));
                return $"raw:{Convert.ToHexStringLower(hash)}";
            }
        }

        return null;
    }

    private static DateTime GetReplayCacheExpiration(SecurityToken token, TokenValidationOptions options)
    {
        var now = DateTime.UtcNow;
        var expires = token switch
        {
            JwtSecurityToken jwtToken when jwtToken.ValidTo != DateTime.MinValue => jwtToken.ValidTo.ToUniversalTime(),
            JsonWebToken jsonWebToken when jsonWebToken.ValidTo != DateTime.MinValue => jsonWebToken.ValidTo.ToUniversalTime(),
            _ => now
        };

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

    private enum TokenReplayRegistrationResult
    {
        Registered,
        ReplayDetected,
        Skipped
    }

    private sealed class ReplayLockState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount;
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
                    OidcAuthenticationLog.OidcTokenValidated(logger, AzureAdScheme);
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
                    OidcAuthenticationLog.OidcTokenValidated(logger, GoogleScheme);
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
            // Generic OIDC is intentionally constrained to authorization code + PKCE.
            options.ResponseType = "code";
            options.UsePkce = true;
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
                    OidcAuthenticationLog.OidcTokenValidated(logger, OidcScheme);
                    return Task.CompletedTask;
                }
            };
        });
    }

    private static void ConfigureOktaAuthentication(
        AuthenticationBuilder builder,
        OktaProviderOptions oktaOptions,
        OidcAuthenticationOptions oidcOptions)
    {
        builder.AddOpenIdConnect(OktaScheme, "Okta", options =>
        {
            options.Authority = oktaOptions.GetAuthority();
            options.ClientId = oktaOptions.ClientId;
            options.ClientSecret = oktaOptions.ClientSecret;
            options.CallbackPath = oktaOptions.CallbackPath;
            options.SignedOutCallbackPath = oktaOptions.SignedOutCallbackPath;
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

            foreach (var scope in oktaOptions.Scopes)
            {
                options.Scope.Add(scope);
            }

            if (oktaOptions.RequestGroupsClaim)
            {
                options.Scope.Add("groups");
            }

            options.Events = new OpenIdConnectEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcAuthenticationFailed(logger, OktaScheme, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcTokenValidated(logger, OktaScheme);
                    return Task.CompletedTask;
                }
            };
        });
    }

    private static void ConfigureAuth0Authentication(
        AuthenticationBuilder builder,
        Auth0ProviderOptions auth0Options,
        OidcAuthenticationOptions oidcOptions)
    {
        builder.AddOpenIdConnect(Auth0Scheme, "Auth0", options =>
        {
            options.Authority = auth0Options.GetAuthority();
            options.ClientId = auth0Options.ClientId;
            options.ClientSecret = auth0Options.ClientSecret;
            options.CallbackPath = auth0Options.CallbackPath;
            options.SignedOutCallbackPath = auth0Options.SignedOutCallbackPath;
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

            foreach (var scope in auth0Options.Scopes)
            {
                options.Scope.Add(scope);
            }

            var audience = auth0Options.Audience;

            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    // Auth0 requires the audience parameter to issue access tokens
                    if (!string.IsNullOrWhiteSpace(audience))
                    {
                        context.ProtocolMessage.SetParameter("audience", audience);
                    }

                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcAuthenticationFailed(logger, Auth0Scheme, context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<OidcAuthenticationOptions>>();
                    OidcAuthenticationLog.OidcTokenValidated(logger, Auth0Scheme);
                    return Task.CompletedTask;
                }
            };
        });
    }

    private static void PopulateAdditionalRoleClaimTypes(OidcAuthenticationOptions options)
    {
        var additionalTypes = new List<string>(options.ClaimsMapping.AdditionalRoleClaimTypes);

        // Okta: add "groups" when RequestGroupsClaim is true
        if (options.Okta?.IsValid == true && options.Okta.RequestGroupsClaim)
        {
            if (!additionalTypes.Contains("groups", StringComparer.OrdinalIgnoreCase))
            {
                additionalTypes.Add("groups");
            }
        }

        // Auth0: add namespace-prefixed roles and permissions when RoleClaimNamespace is set
        if (options.Auth0?.IsValid == true && !string.IsNullOrWhiteSpace(options.Auth0.RoleClaimNamespace))
        {
            var ns = options.Auth0.RoleClaimNamespace.TrimEnd('/');
            var rolesClaim = $"{ns}/roles";
            var permissionsClaim = $"{ns}/permissions";

            if (!additionalTypes.Contains(rolesClaim, StringComparer.OrdinalIgnoreCase))
            {
                additionalTypes.Add(rolesClaim);
            }

            if (!additionalTypes.Contains(permissionsClaim, StringComparer.OrdinalIgnoreCase))
            {
                additionalTypes.Add(permissionsClaim);
            }
        }

        options.ClaimsMapping.AdditionalRoleClaimTypes = additionalTypes.ToArray();
    }
}
