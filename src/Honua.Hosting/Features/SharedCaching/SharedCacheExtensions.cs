// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.SharedCaching;

internal static class SharedCacheExtensions
{
    internal static IServiceCollection AddSharedCachePolicy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SharedCacheOptions>().Bind(configuration.GetSection(SharedCacheOptions.SectionName));
        services.AddSingleton<IConfigurationDocumentationContributor, SharedCacheConfigurationDocumentationContributor>();
        services.TryAddSingleton<ICdnInvalidator, NoCdnInvalidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CdnInvalidationService>();
        return services;
    }

    internal static TBuilder WithSharedCacheClass<TBuilder>(this TBuilder builder, ResponseCacheClass cacheClass)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new SharedCacheClassMetadata(cacheClass));
        builder.AddEndpointFilter(static async (invocation, next) =>
        {
            var result = await next(invocation).ConfigureAwait(false);
            SharedCacheResponse.Apply(invocation.HttpContext);
            return result;
        });
        return builder;
    }
}
