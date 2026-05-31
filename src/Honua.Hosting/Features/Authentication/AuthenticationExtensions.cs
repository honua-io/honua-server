// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;
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
    /// Authorization policy name for temporal-history read access (honua-server#1166).
    /// Distinct from the current-read and admin surfaces so it can be tightened to a
    /// dedicated permission grant in a later slice without touching endpoint code. In
    /// this slice it requires the admin role, matching the admin baseline.
    /// </summary>
    public const string TemporalHistoryReadPolicy = "TemporalHistoryRead";

    /// <summary>
    /// Adds API key authentication and authorization services
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services)
    {
        services.AddScoped<ApiKeyAuthenticationDependencies>();

        // Add authentication with API key scheme
        _ = services.AddAuthentication(defaultScheme: ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyScheme,
                options => { });

        // Add authorization policies
        _ = services.AddAuthorization(options =>
        {
            options.AddPolicy(AdminPolicy, policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("admin");
                policy.AuthenticationSchemes.Add(ApiKeyScheme);
                policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
            });

            options.AddPolicy(AdminPolicyAlias, policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("admin");
                policy.AuthenticationSchemes.Add(ApiKeyScheme);
                policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
            });

            options.AddPolicy(TemporalHistoryReadPolicy, policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("admin");
                policy.AuthenticationSchemes.Add(ApiKeyScheme);
                policy.AuthenticationSchemes.Add(ClientCertificateAuthenticationDefaults.AuthenticationScheme);
            });

        });

        return services;
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
    /// Requires the distinct temporal-history read authorization for an endpoint or group
    /// (honua-server#1166).
    /// </summary>
    public static TBuilder RequireTemporalHistoryRead<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(TemporalHistoryReadPolicy);
}
