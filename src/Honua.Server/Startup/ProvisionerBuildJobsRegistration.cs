// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Provisioner.BuildJobs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Server.Startup;

/// <summary>
/// Registers the per-area geocoder/router build job submission service and its
/// config-driven backend options. Mirrors the batch-dispatched tile-cache registration:
/// the submission service depends on the durable execution-job store/queue, which are only
/// present when Redis is configured, so registration is gated on the Redis multiplexer to
/// keep <c>GetService&lt;IProvisionerBuildJobService&gt;()</c> from throwing on a missing
/// dependency in stores-less dev/test profiles. The build jobs ride the same GP-on-Batch
/// dispatch path (durable record → reconciler → IBatchComputeBackend) that tiling uses.
/// </summary>
internal static class ProvisionerBuildJobsRegistration
{
    public static IServiceCollection AddHonuaProvisionerBuildJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ProvisionerBuildBatchOptions>(
            configuration.GetSection(ProvisionerBuildBatchOptions.SectionName));

        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IProvisionerBuildJobService, ProvisionerBuildJobService>();
        }

        return services;
    }
}
