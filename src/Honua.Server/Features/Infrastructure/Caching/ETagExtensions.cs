// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Middleware;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Extension methods for configuring ETag support.
/// </summary>
internal static class ETagExtensions
{
    /// <summary>
    /// Adds ETag services to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    internal static IServiceCollection AddETags(this IServiceCollection services)
    {
        services.AddSingleton<IETagService, ETagService>();
        return services;
    }

    /// <summary>
    /// Adds the ETag middleware to the application pipeline.
    /// Should be called after UseOutputCache() to work with the existing cache infrastructure.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    internal static IApplicationBuilder UseETags(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ETagMiddleware>();
    }

    /// <summary>
    /// Configures an endpoint to use ETags by adding the endpoint filter.
    /// </summary>
    /// <param name="builder">The route handler builder</param>
    /// <returns>The route handler builder for chaining</returns>
    internal static RouteHandlerBuilder WithETag(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter<ETagEndpointFilter>();
    }

    /// <summary>
    /// Configures an endpoint to use ETags when only a convention builder is available.
    /// </summary>
    /// <param name="builder">The endpoint convention builder</param>
    /// <returns>The endpoint convention builder for chaining</returns>
    internal static IEndpointConventionBuilder WithETag(this IEndpointConventionBuilder builder)
    {
        if (builder is RouteHandlerBuilder routeHandlerBuilder)
        {
            routeHandlerBuilder.AddEndpointFilter<ETagEndpointFilter>();
        }

        return builder;
    }
}
