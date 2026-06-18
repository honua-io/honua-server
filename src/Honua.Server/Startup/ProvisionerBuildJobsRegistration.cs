// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Provisioner.BuildJobs;
using Microsoft.Extensions.DependencyInjection;
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

        // Worker-side executors for the per-area geocoder/router build kinds. Registered
        // unconditionally (mirroring TileCacheJobExecutor) so any worker host can claim these
        // kinds from the in-process queue. Without them the default zero-config `local` backend
        // enqueues GeocoderBuild/RouterBuild jobs that JobExecutionService never claims (no
        // executor for the kind), leaving them Queued forever. One descriptor per kind because
        // JobExecutionService maps executors one-to-one by IJobExecutor.Kind. Both are the same
        // implementation type with a different Kind, so they are registered as distinct
        // factory-built singletons (TryAddEnumerable would dedupe them by type and drop one); this
        // method has a single call site so there is no double-registration risk.
        services.AddSingleton<IJobExecutor>(sp =>
            ProvisionerBuildJobExecutor.ForGeocoder(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetRequiredService<ILogger<ProvisionerBuildJobExecutor>>()));
        services.AddSingleton<IJobExecutor>(sp =>
            ProvisionerBuildJobExecutor.ForRouter(
                sp.GetRequiredService<IUniversalProgressStore>(),
                sp.GetRequiredService<ILogger<ProvisionerBuildJobExecutor>>()));

        if (services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IProvisionerBuildJobService, ProvisionerBuildJobService>();
        }

        return services;
    }
}
