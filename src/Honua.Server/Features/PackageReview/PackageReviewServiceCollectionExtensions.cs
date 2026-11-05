// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.PackageReview;
using Honua.Core.Features.PackageReview.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.PackageReview;

internal static class PackageReviewServiceCollectionExtensions
{
    public static IServiceCollection AddPackageReview(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPackageReviewCore();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPackageFamilyReviewAdapter, AnalysisPlanPackageReviewAdapter>());

        return services;
    }
}
