// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.SharedCaching;

internal static class SharedCacheExtensions
{
    internal static IServiceCollection AddSharedCachePolicy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SharedCacheOptions>().Bind(configuration.GetSection(SharedCacheOptions.SectionName));
        services.AddSingleton<IConfigurationDocumentationContributor, SharedCacheConfigurationDocumentationContributor>();
        services.TryAddSingleton<ICdnInvalidator, NoCdnInvalidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static provider =>
        {
            var shared = new CdnPurgeCoordination
            {
                TimeProvider = provider.GetRequiredService<TimeProvider>(),
                HttpContextAccessor = provider.GetService<IHttpContextAccessor>(),
                Options = provider.GetService<IOptionsMonitor<SharedCacheOptions>>(),
                LeaseStore = provider.GetService<ILeaseStore>(),
                Budget = provider.GetService<ICdnPurgeBudget>(),
            };
            var reliability = new CdnPurgeReliability(
                provider.GetRequiredService<ICdnInvalidator>(),
                provider.GetRequiredService<ILogger<CdnPurgeReliability>>(),
                shared);
            return new CdnPurgeCoordination
            {
                TimeProvider = shared.TimeProvider,
                HttpContextAccessor = shared.HttpContextAccessor,
                Options = shared.Options,
                LeaseStore = shared.LeaseStore,
                Budget = shared.Budget,
                Reliability = reliability,
            };
        });
        services.TryAddSingleton(static provider => provider.GetRequiredService<CdnPurgeCoordination>().Reliability!);
        services.TryAddSingleton<CdnInvalidationService>();
        services.AddHostedService<CdnPendingPurgeFlush>();
        return services;
    }

    internal static TBuilder WithSharedCacheClass<TBuilder>(this TBuilder builder, ResponseCacheClass cacheClass)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new SharedCacheClassMetadata(cacheClass));
        builder.WithETag();
        builder.AddEndpointFilter(static async (invocation, next) =>
        {
            var result = await next(invocation).ConfigureAwait(false);
            result = await SharedResponseETag.ApplyAsync(invocation.HttpContext, result).ConfigureAwait(false);
            SharedCacheResponse.Apply(invocation.HttpContext);
            return result;
        });
        return builder;
    }
}
