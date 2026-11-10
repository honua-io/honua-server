// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Core.Features.PackageReview.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.PackageReview;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPackageReviewCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPackageReviewService, PackageReviewService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPackageFamilyReviewAdapter, RequirementPackageReviewAdapter>());

        return services;
    }
}
