// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Registers geoprocessing workspace lifecycle services.
/// </summary>
internal static class GeoprocessingServiceCollectionExtensions
{
    public static IServiceCollection AddGeoprocessing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<WorkspaceOptions>(
            configuration.GetSection(WorkspaceOptions.SectionName));

        services.AddSingleton<IRetentionPolicyEvaluator, RetentionPolicyEvaluator>();
        services.AddScoped<IWorkspaceLifecycleService, WorkspaceLifecycleService>();
        services.AddHostedService<WorkspaceCleanupService>();

        return services;
    }
}
