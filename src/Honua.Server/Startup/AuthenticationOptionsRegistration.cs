// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Mobile.Auth;

namespace Honua.Server.Startup;

/// <summary>
/// Binds and validates authentication-related options (API key / OIDC / mobile / RBAC /
/// operator approval) and registers operator authorization plumbing. The dev-auth bypass
/// startup validator runs in <c>Program.cs</c> before this method so misconfigured deploys
/// fail before any auth handler executes; once invoked here the API-key options block has
/// already been validated against the resolved environment + admin password complexity rules.
/// </summary>
internal static class AuthenticationOptionsRegistration
{
    public static IServiceCollection AddHonuaAuthenticationOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // API key authentication options (admin password complexity is enforced in production)
        services.Configure<ApiKeyAuthenticationOptions>(options =>
        {
            options.IsDevelopmentMode = environment.IsDevelopment();
            options.IsTestMode = environment.IsEnvironment("Test");
            var adminPassword = configuration["HONUA_ADMIN_PASSWORD"];

            // SECURITY: Validate admin password complexity in production
            if (environment.IsProduction() && !string.IsNullOrEmpty(adminPassword))
            {
                if (adminPassword.Length < 16)
                {
                    throw new InvalidOperationException("Admin password must be at least 16 characters in production environment");
                }

                bool hasUpper = adminPassword.Any(char.IsUpper);
                bool hasLower = adminPassword.Any(char.IsLower);
                bool hasDigit = adminPassword.Any(char.IsDigit);
                bool hasSpecial = adminPassword.Any(c => !char.IsLetterOrDigit(c));

                if (!(hasUpper && hasLower && hasDigit && hasSpecial))
                {
                    throw new InvalidOperationException("Admin password must contain uppercase, lowercase, digit, and special characters in production environment");
                }
            }

            options.AdminPassword = adminPassword;
            options.DevAuthBypass = configuration["HONUA_DEV_AUTH"];
            options.DevAuthBypassAcknowledged = configuration["HONUA_DEV_AUTH_ALLOW_BYPASS"];
            options.EnvironmentName = environment.EnvironmentName;
            options.EnableBasicAuthCompatibility =
                configuration.GetValue("Authentication:BasicCompatibility:Enabled",
                    configuration.GetValue("HONUA_ENABLE_BASIC_AUTH_COMPAT", false));

            // SECURITY: Always enforce HTTPS for basic auth in production - cannot be overridden
            if (environment.IsProduction())
            {
                options.RequireHttpsForBasicAuth = true;
            }
            else
            {
                // In development/test environments, allow configuration override
                var requireHttpsForBasicAuth = configuration.GetValue("Authentication:BasicCompatibility:RequireHttps",
                    configuration.GetValue("HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", true));
                options.RequireHttpsForBasicAuth = requireHttpsForBasicAuth;
            }
        });

        // OIDC authentication options
        services.Configure<OidcAuthenticationOptions>(
            configuration.GetSection(OidcAuthenticationOptions.SectionName));

        // Console-consumable operator bearer (#2258, Option C). Registered
        // unconditionally so the issue endpoint can report a fail-closed 503 when the
        // feature is not configured; the request-path scheme is only wired when OIDC
        // is enabled (operator login itself requires OIDC).
        services.Configure<OperatorBearerOptions>(
            configuration.GetSection(OperatorBearerOptions.SectionName));
        services.AddSingleton<OperatorBearerTokenService>();

        // Mobile runtime auth refresh options
        services.Configure<MobileAuthOptions>(
            configuration.GetSection(MobileAuthOptions.SectionName));

        // RBAC options
        services.Configure<RbacOptions>(
            configuration.GetSection(RbacOptions.SectionName));

        // Operator authorization and approval
        services.AddSingleton<IOperatorAuthorizationEvaluator, OperatorAuthorizationEvaluator>();
        services.AddSingleton<IOperatorApprovalEvaluator, DefaultOperatorApprovalEvaluator>();

        // OAuth 2.1 scope narrowing (#2851): intersects a bearer token's scopes with the
        // operator grant model so a scope can only narrow, never widen, what the principal's
        // grants already permit. Non-OAuth principals (X-API-Key, interactive) are untouched.
        services.AddSingleton<IOperatorScopeAuthorizer,
            Honua.Core.Features.Authorization.OperatorScopeAuthorizer>();

        // AI-operations guardrail ladder by edition (#1631): the shared,
        // protocol-neutral policy that maps edition -> guardrail level
        // (Community direct, Pro validation layer, Enterprise approval + policy).
        services.AddSingleton<IAgentGuardrailPolicy,
            Honua.Core.Features.Authorization.EditionAgentGuardrailPolicy>();
        services.Configure<OperatorApprovalOptions>(
            configuration.GetSection(OperatorApprovalOptions.SectionName));
        services.AddScoped<OperatorApprovalGate>();

        // Authentication and authorization handlers
        services.AddApiKeyAuthentication(configuration);
        services.AddHonuaClientCertificateAuthentication(configuration);
        services.AddOidcAuthentication(configuration);
        services.AddOidcAuthorization(configuration);
        services.AddPortalTokenAuthentication(configuration);
        services.AddSingleton<AdminAuthSessionStore>();

        return services;
    }
}
