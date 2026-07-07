// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// DI registration for the aggregated operate-status feature (A12): the SLO options and the
/// composing service.
/// </summary>
internal static class OperateStatusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the aggregated operate-status service and binds the SLO configuration. Scoped
    /// because it composes the scoped ops-health snapshot and ops-findings services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddOperateStatus(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OperateSloOptions>()
            .Bind(configuration.GetSection(OperateSloOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IOperateStatusService, OperateStatusService>();

        return services;
    }
}
