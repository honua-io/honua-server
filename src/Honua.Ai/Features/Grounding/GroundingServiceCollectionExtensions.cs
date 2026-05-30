// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Grounding.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Grounding;

/// <summary>
/// DI registration for the grounding feature slice. The engine and
/// authorization filter are stateless and compiled against frozen catalogs,
/// so they register as singletons. <see cref="IGroundingService"/> is also a
/// singleton; it consumes the Metadata V2 graph provider per call through
/// <see cref="IServiceScopeFactory"/> so each request binds to a fresh scoped
/// graph reader (and its scoped database dependencies) without
/// capturing one for the lifetime of the root provider.
/// </summary>
internal static class GroundingServiceCollectionExtensions
{
    public static IServiceCollection AddGroundingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GroundingOptions>()
            .Bind(configuration.GetSection(GroundingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GroundingOptions>, GroundingOptionsValidator>());

        services.TryAddSingleton<IGroundingEngine, DeterministicGroundingEngine>();
        services.TryAddSingleton<IGroundingAuthorizationFilter, OperatorGroundingAuthorizationFilter>();
        services.TryAddSingleton<IGroundingService, GroundingService>();

        return services;
    }
}
