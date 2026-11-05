// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal static class ETagExtensions
{
    internal static IServiceCollection AddETags(this IServiceCollection services)
    {
        services.AddSingleton<IETagService, ETagService>();
        return services;
    }

    internal static RouteHandlerBuilder WithETag(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter<ETagEndpointFilter>();
    }

    internal static IEndpointConventionBuilder WithETag(this IEndpointConventionBuilder builder)
    {
        if (builder is RouteHandlerBuilder routeHandlerBuilder)
        {
            routeHandlerBuilder.AddEndpointFilter<ETagEndpointFilter>();
        }

        return builder;
    }
}
