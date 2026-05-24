// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.PackageReview;
using Honua.Core.Features.PackageReview.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.PackageReview;

/// <summary>
/// Dependency-injection wiring for package validation and preview planning.
/// </summary>
internal static class PackageReviewServiceCollectionExtensions
{
    /// <summary>
    /// Registers package-review services and server-side family adapters.
    /// </summary>
    public static IServiceCollection AddPackageReview(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPackageReviewCore();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPackageFamilyReviewAdapter, AnalysisPlanPackageReviewAdapter>());

        return services;
    }
}
