// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Honua.Infrastructure.Authentication.ClientCertificates;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Extension methods for configuring authentication services
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Authentication scheme name for API key authentication
    /// </summary>
    public const string ApiKeyScheme = "ApiKey";

    /// <summary>
    /// Authorization policy name for admin access
    /// </summary>
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Authorization policy name for admin access (alias for legacy endpoints)
    /// </summary>
    public const string AdminPolicyAlias = "AdminPolicy";

    /// <summary>
    /// Authorization policy name for the read-only operational-observability surfaces (A12).
    /// Admits the admin family (full admin or <c>admin:read</c>) or a dedicated read-only
    /// <c>ops:read</c> credential for safe reads, while still requiring full admin write for any
    /// mutating request routed through it. Distinct from <see cref="AdminPolicy"/> so an ops-reader
    /// key can observe the ops posture without holding a credential that could mutate.
    /// </summary>
    public const string OpsReadPolicy = "OpsRead";

    /// <summary>
    /// Authorization policy name for temporal-history read access (honua-server#1166).
    /// Distinct from the current-read and admin surfaces so it can be tightened to a
    /// dedicated permission grant in a later slice without touching endpoint code. In
    /// this slice it requires the admin role, matching the admin baseline.
    /// </summary>
    public const string TemporalHistoryReadPolicy = "TemporalHistoryRead";

    /// <summary>
    /// Authorization policy name for temporal-diff read access (honua-server#1166 slice 2). Distinct from
    /// history reads so diff comparison between checkpoints can be authorized separately. In this slice it
    /// requires the admin role, matching the admin baseline.
    /// </summary>
    public const string TemporalDiffReadPolicy = "TemporalDiffRead";

    /// <summary>
    /// Authorization policy name for governed rollback execution (honua-server#1166 slice 5). Distinct
    /// from read policies so the mutating corrective-operation surface is authorized separately. In this
    /// slice it requires the admin role, matching the admin baseline.
    /// </summary>
    public const string TemporalRollbackExecutePolicy = "TemporalRollbackExecute";

    /// <summary>
    /// Adds API key authentication and authorization services
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<ApiKeyAuthenticationDependencies>();

        // Required by AdminPermissionAuthorizationHandler to read the request method
        // when authorization is evaluated outside the endpoint resource (#1985).
        services.AddHttpContextAccessor();

        // Enforces scoped admin API-key permissions on the general request path
        // (#1985). Registered as a singleton authorization handler so it participates
        // in every policy that adds AdminPermissionRequirement below.
        services.AddSingleton<IAuthorizationHandler, AdminPermissionAuthorizationHandler>();

        // Enforces the read-only ops-reader split (A12): admits admin or ops:read grants for safe
        // reads while requiring full admin write for mutating requests routed through OpsReadPolicy.
        services.AddSingleton<IAuthorizationHandler, OpsReadAuthorizationHandler>();

        // Add authentication with API key scheme
        _ = services.AddAuthentication(defaultScheme: ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyScheme,
                options => { });

        // #2958: the client-certificate scheme is only ever a valid policy scheme
        // reference when the security.mtls experimental capability is enabled — resolved
        // once here (composition time) rather than per-policy-evaluation.
        var mtlsCapabilityEnabled = ClientCertificateAuthenticationExtensions.IsMtlsCapabilityEnabled(configuration);

        // Add authorization policies
        _ = services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminPolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(AdminPolicyAlias, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(OpsReadPolicy, policy => ConfigureOpsReadPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(TemporalHistoryReadPolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(TemporalDiffReadPolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
            options.AddPolicy(TemporalRollbackExecutePolicy, policy => ConfigureAdminPolicy(policy, mtlsCapabilityEnabled));
        });

        return services;
    }

    /// <summary>
    /// Configures an admin-family authorization policy: an authenticated principal
    /// in the <c>admin</c> role whose scoped permission grants authorize the request
    /// (#1985). The role check preserves the legacy gate; the permission requirement
    /// adds scoped-key enforcement so a read-only admin key cannot mutate.
    /// </summary>
    /// <param name="policy">The policy builder to configure.</param>
    /// <param name="mtlsCapabilityEnabled">
    /// Whether the <c>security.mtls</c> experimental capability is enabled (#2958). Only
    /// then is the client-certificate scheme added to the policy's accepted schemes — while
    /// disabled (the default), a valid bearer token authenticates admin requests with no
    /// interposed cert-RBAC check.
    /// </param>
    private static void ConfigureAdminPolicy(AuthorizationPolicyBuilder policy, bool mtlsCapabilityEnabled)
    {
        _ = policy.RequireAuthenticatedUser();
        _ = policy.RequireRole("admin");
        _ = policy.AddRequirements(new AdminPermissionRequirement());
        policy.AuthenticationSchemes.Add(ApiKeyScheme);
        if (mtlsCapabilityEnabled)
        {
            policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
        }
    }

    /// <summary>
    /// Configures the read-only ops-reader authorization policy (A12). Unlike the admin family it
    /// does NOT require the <c>admin</c> role — a key minted with only an <c>ops:read</c> grant
    /// authenticates as a non-admin scoped principal — so authorization is decided by
    /// <see cref="OpsReadRequirement"/> (via <see cref="AdminApiKeyPermission.IsOpsReadAuthorized"/>):
    /// admin (any level) or ops:read for safe methods; full admin write for mutating methods.
    /// </summary>
    /// <param name="policy">The policy builder to configure.</param>
    /// <param name="mtlsCapabilityEnabled">
    /// Whether the <c>security.mtls</c> experimental capability is enabled (#2958); see
    /// <see cref="ConfigureAdminPolicy"/>.
    /// </param>
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

    /// <summary>
    /// Adds authentication middleware to the request pipeline
    /// </summary>
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app)
    {
        return app.UseAuthentication()
                  .UseAuthorization();
    }

    /// <summary>
    /// Requires admin authorization for an endpoint or endpoint group
    /// </summary>
    public static TBuilder RequireAdminAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(AdminPolicy);

    /// <summary>
    /// Requires read-only ops-reader authorization for an endpoint or group (A12). Safe reads are
    /// authorized by an admin key (any level) or a read-only <c>ops:read</c> grant; a mutating
    /// request routed through this policy still requires full admin write, so applying it to a mixed
    /// read/write group keeps every mutation admin-only while additionally admitting ops-reader keys
    /// to the reads.
    /// </summary>
    public static TBuilder RequireOpsReadAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(OpsReadPolicy);

    /// <summary>
    /// Requires the distinct temporal-history read authorization for an endpoint or group
    /// (honua-server#1166).
    /// </summary>
    public static TBuilder RequireTemporalHistoryRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalHistoryReadPolicy);

    /// <summary>
    /// Requires the distinct temporal-diff read authorization for an endpoint or group
    /// (honua-server#1166 slice 2).
    /// </summary>
    public static TBuilder RequireTemporalDiffRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalDiffReadPolicy);

    /// <summary>
    /// Requires the distinct governed-rollback execution authorization for an endpoint or group
    /// (honua-server#1166 slice 5).
    /// </summary>
    public static TBuilder RequireTemporalRollbackExecute<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalRollbackExecutePolicy);
}
