// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec;
using Honua.Core.Features.Spec.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Spec;

/// <summary>
/// Dependency-injection wiring for the spec plan / apply vertical slice.
/// </summary>
internal static class SpecServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Core planner / apply engine and the server-side compute
    /// executor used by the <c>/v1/spec/*</c> surface and the gRPC
    /// <c>SpecService</c>.
    /// </summary>
    public static IServiceCollection AddSpec(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSpecCore(configuration);

        services.TryAddSingleton<ISpecComputeExecutor>(_ => new SpecComputeExecutor());

        return services;
    }
}
