// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Studio;

/// <summary>
/// Dependency injection helpers for Studio package lifecycle services.
/// </summary>
public static class StudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared Studio package lifecycle service and fallback in-memory store.
    /// </summary>
    public static IServiceCollection AddStudioPackageLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        services.TryAddScoped<IStudioPackageFamilyRegistry, StudioPackageFamilyRegistry>();
        services.TryAddScoped<IStudioPackageValidator, StudioPackageValidator>();
        services.TryAddScoped<IStudioPackageLifecycleService, StudioPackageLifecycleService>();
        return services;
    }
}
