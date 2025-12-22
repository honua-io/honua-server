// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Authentication;

namespace Honua.Server.Features.Infrastructure.Authentication;

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
    /// Adds API key authentication and authorization services
    /// </summary>
    public static IServiceCollection AddApiKeyAuthentication(this IServiceCollection services)
    {
        // Add authentication with API key scheme
        _ = services.AddAuthentication(defaultScheme: ApiKeyScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyScheme,
                options => { });

        // Add authorization with admin policy
        _ = services.AddAuthorization(options => options.AddPolicy(AdminPolicy, policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.RequireRole("admin");
                policy.AuthenticationSchemes.Add(ApiKeyScheme);
            }));

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
}
