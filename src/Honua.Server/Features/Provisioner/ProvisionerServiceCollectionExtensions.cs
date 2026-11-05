// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Provisioner.BuildJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Provisioner;

internal static class ProvisionerServiceCollectionExtensions
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

        if (services.Any(d => d.ServiceType == typeof(IExecutionJobStore)))
        {
            services.TryAddSingleton<IProvisionerBuildJobService, ProvisionerBuildJobService>();
        }

        return services;
    }
}
