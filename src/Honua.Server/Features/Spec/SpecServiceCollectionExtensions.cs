// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec;
using Honua.Core.Features.Spec.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Spec;

internal static class SpecServiceCollectionExtensions
{
    public static IServiceCollection AddSpec(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSpecCore(configuration);

        services.TryAddSingleton<ISpecComputeExecutor>(_ => new SpecComputeExecutor());

        return services;
    }
}
