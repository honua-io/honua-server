// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.PackageReview;

/// <summary>
/// Service registration helpers for package validation and read-only preview planning.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the canonical package-review service and shared requirement adapter.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddPackageReviewCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPackageReviewService, PackageReviewService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPackageFamilyReviewAdapter, RequirementPackageReviewAdapter>());

        return services;
    }
}
