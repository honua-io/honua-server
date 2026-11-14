// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

public static partial class OidcAuthenticationExtensions
{
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
}
