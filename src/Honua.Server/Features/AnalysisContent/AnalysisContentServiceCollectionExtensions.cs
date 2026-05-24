// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AnalysisContent.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.AnalysisContent;

internal static class AnalysisContentServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisContent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAnalysisContentStore, InMemoryAnalysisContentStore>();
        services.TryAddScoped<IAnalysisContentService, AnalysisContentService>();

        return services;
    }
}
