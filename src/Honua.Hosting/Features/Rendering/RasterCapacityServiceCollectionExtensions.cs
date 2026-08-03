// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Capacity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Registers provider-neutral synchronous raster admission.
/// </summary>
public static class RasterCapacityServiceCollectionExtensions
{
    /// <summary>
    /// Adds an AOT-safe, no-native capacity implementation for the serving process.
    /// </summary>
    public static IServiceCollection AddRasterCapacityAdmission(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RasterCapacityOptions>()
            .Bind(configuration.GetSection(RasterCapacityOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.MaxConcurrentRequestsPerTenant <= options.MaxConcurrentRequests,
                $"{RasterCapacityOptions.SectionName}:MaxConcurrentRequestsPerTenant cannot exceed MaxConcurrentRequests.")
            .ValidateOnStart();
        services.TryAddSingleton<IRasterCapacityAdmission, InMemoryRasterCapacityAdmission>();
        return services;
    }
}
