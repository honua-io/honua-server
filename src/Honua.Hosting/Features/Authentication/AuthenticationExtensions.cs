// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Honua.Infrastructure.Authentication.ClientCertificates;

namespace Honua.Infrastructure.Authentication;

public static class AuthenticationExtensions
{
    public const string ApiKeyScheme = "ApiKey";

    public const string AdminPolicy = "Admin";

    public const string AdminPolicyAlias = "AdminPolicy";

    public const string AdminApprovePolicy = "AdminApprove";

    public const string AdminCredentialManagementPolicy = "AdminCredentialManagement";

    // The ops-reader policy admits admin or ops:read for a safe method and still requires full admin for a mutation on that same policy.
    public const string OpsReadPolicy = "OpsRead";

    public const string TemporalHistoryReadPolicy = "TemporalHistoryRead";

    public const string TemporalDiffReadPolicy = "TemporalDiffRead";

    public const string TemporalRollbackExecutePolicy = "TemporalRollbackExecute";

    // Conformance mutation is a separate policy from admin so the runner credential can be narrowed later, and the per-run ownership token isolates one authorized run from another.
    public const string ConformanceMutatePolicy = "ConformanceMutate";

    public const string StudioLifecyclePolicy = "StudioLifecycle";

    public const string StudioAiProxyPolicy = "StudioAiProxy";

    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<ApiKeyAuthenticationDependencies>();

        // Live managed-user membership when OIDC is off. AddOidcAuthentication registers
        // its own transformation later and calls the same revalidation itself.
        services.TryAddScoped<IClaimsTransformation, ManagedMembershipClaimsTransformation>();

        // Required by AdminPermissionAuthorizationHandler to read the request method
        // when authorization is evaluated outside the endpoint resource (#1985).
        services.AddHttpContextAccessor();

        // Enforces scoped admin API-key permissions on the general request path
        // (#1985). Registered as a singleton authorization handler so it participates
        // in every policy that adds AdminPermissionRequirement below.
        services.AddSingleton<IAuthorizationHandler, AdminPermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, AdminApproveAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, AdminCredentialManagementAuthorizationHandler>();

        // Enforces the read-only ops-reader split (A12): admits admin or ops:read grants for safe
        // reads while requiring full admin write for mutating requests routed through OpsReadPolicy.
        services.AddSingleton<IAuthorizationHandler, OpsReadAuthorizationHandler>();

        // Widens the Studio package lifecycle surface from admin-only to ownership-scoped end
        // users behind a feature flag (honua-server#3001).
        services.AddSingleton<IAuthorizationHandler, StudioLifecycleAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, StudioAiProxyAuthorizationHandler>();
        // Records the 401/403 outcome of every policy the authorization middleware evaluates —
        // those denials short-circuit above the audit middleware and are observable nowhere else.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, HonuaAuthorizationMiddlewareResultHandler>();

        services.AddTransient<ScopedJobAuthenticationDependencies>();

        // Add authentication with API key scheme
        _ = services.AddAuthentication(defaultScheme: ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyScheme,
                options => { })
            .AddScheme<AuthenticationSchemeOptions, ScopedJobTokenAuthenticationHandler>(
                ScopedJobAuthentication.Scheme, options => { });

        // #2958: the client-certificate scheme is only ever a valid policy scheme
        // reference when the security.mtls experimental capability is enabled — resolved
        // once here (composition time) rather than per-policy-evaluation.
        var mtlsCapabilityEnabled = ClientCertificateAuthenticationExtensions.IsMtlsCapabilityEnabled(configuration);

        // honua-server#3001: whether OIDC is configured at all, resolved once here so
        // ConfigureStudioLifecyclePolicy can route through the composite ApiKey/JWT-Bearer
        // scheme (the only way an OIDC end-user session can satisfy the policy) instead of
        // referencing a scheme that was never registered when OIDC is off.
        var oidcEnabled = configuration.GetValue<bool>($"{OidcAuthenticationOptions.SectionName}:Enabled");

        // Add authorization policies
        _ = services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminPolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(AdminPolicyAlias, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(AdminApprovePolicy, policy => ConfigureAdminApprovePolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(AdminCredentialManagementPolicy, policy =>
            {
                ConfigureAdminPolicy(policy, mtlsCapabilityEnabled);
                _ = policy.AddRequirements(new AdminCredentialManagementRequirement());
            });
            options.AddPolicy(OpsReadPolicy, policy => ConfigureOpsReadPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(TemporalHistoryReadPolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(TemporalDiffReadPolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(TemporalRollbackExecutePolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(ConformanceMutatePolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(StudioLifecyclePolicy, policy => ConfigureStudioLifecyclePolicy(policy, mtlsCapabilityEnabled, oidcEnabled));
            options.AddPolicy(StudioAiProxyPolicy, policy =>
            {
                ConfigureStudioLifecyclePolicy(policy, mtlsCapabilityEnabled, oidcEnabled);
                _ = policy.AddRequirements(new StudioAiProxyRequirement());
            });
        });

        return services;
    }

    // While mTLS is disabled the certificate scheme is not registered, so a valid bearer authenticates an admin request with no certificate check.
    private static void ConfigureAdminPolicy(AuthorizationPolicyBuilder policy, bool mtlsCapabilityEnabled)
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.RequireRole("admin", AdminApiKeyPermission.ApprovedOperationRole, AdminApiKeyPermission.ScopedAdminRole);
        _ = policy.AddRequirements(new AdminPermissionRequirement());
        policy.AuthenticationSchemes.Add(ApiKeyScheme);
        if (mtlsCapabilityEnabled)
        {
            policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static void ConfigureAdminApprovePolicy(AuthorizationPolicyBuilder policy, bool mtlsCapabilityEnabled)
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.RequireRole("admin", AdminApiKeyPermission.ScopedAdminRole);
        _ = policy.AddRequirements(new AdminApproveRequirement());
        policy.AuthenticationSchemes.Add(ApiKeyScheme);
        if (mtlsCapabilityEnabled)
        {
            policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
        }
    }

    // The ops-reader policy does not require the admin role.
    private static void ConfigureOpsReadPolicy(AuthorizationPolicyBuilder policy, bool mtlsCapabilityEnabled)
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.AddRequirements(new OpsReadRequirement());
        policy.AuthenticationSchemes.Add(ApiKeyScheme);
        if (mtlsCapabilityEnabled)
        {
            policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
        }
    }

    // Pin this policy to the composite scheme only while OIDC is registered: leaving it unpinned hides an OIDC principal, and naming an unregistered scheme throws.
    private static void ConfigureStudioLifecyclePolicy(AuthorizationPolicyBuilder policy, bool mtlsCapabilityEnabled, bool oidcEnabled)
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.AddRequirements(new StudioLifecycleRequirement());
        if (oidcEnabled)
        {
            policy.AuthenticationSchemes.Add(OidcAuthenticationExtensions.CompositeScheme);
        }
        else
        {
            policy.AuthenticationSchemes.Add(ApiKeyScheme);
            if (mtlsCapabilityEnabled)
            {
                policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
            }
        }
    }

    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app)
    {
        return app.UseAuthentication()
                  .UseAuthorization();
    }

    public static TBuilder RequireAdminAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(AdminPolicy);

    public static TBuilder RequireAdminCredentialManagementAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(AdminCredentialManagementPolicy);

    public static TBuilder RequireAdminApproveAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(AdminApprovePolicy);

    public static TBuilder RequireOpsReadAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(OpsReadPolicy);

    public static TBuilder RequireTemporalHistoryRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalHistoryReadPolicy);

    public static TBuilder RequireTemporalDiffRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalDiffReadPolicy);

    public static TBuilder RequireTemporalRollbackExecute<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalRollbackExecutePolicy);

    public static TBuilder RequireConformanceMutateAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(ConformanceMutatePolicy);

    // With the Studio flag off this policy is the admin policy, and elevated publish or rollback checks stay in the handler.
    public static TBuilder RequireStudioLifecycleAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(StudioLifecyclePolicy);

    // A non-admin Studio AI caller must be an interactive OIDC or session identity, not an API key.
    public static TBuilder RequireStudioAiProxyAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(StudioAiProxyPolicy);
}
