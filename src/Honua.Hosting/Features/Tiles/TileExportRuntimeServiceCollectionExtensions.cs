// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Tiles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

public static class TileExportRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddTileExportRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<ITileExportJobService>(static sp => new TileExportJobService(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<CloudStorageOptions>>(),
            sp.GetRequiredService<ILogger<TileExportJobService>>(),
            sp.GetService<IExecutionJobStore>(),
            sp.GetService<IJobQueue>(),
            sp.GetService<ICloudFileStorage>(),
            sp.GetService<IExecutionAdmissionEvaluator>(),
            sp.GetService<ExecutionAdmissionCoordinator>(),
            sp.GetService<IOptions<RbacOptions>>(),
            sp.GetService<ITenantContext>()));

        // One coordinator per process, shared with geoprocessing when that module is also
        // composed (TryAddSingleton). A tiles-only host still gets the shared lease when Redis
        // and admission options are already registered; otherwise SubmitAsync uses ProcessLocal.
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer))
            && services.Any(static descriptor => descriptor.ServiceType == typeof(IConfigureOptions<ExecutionAdmissionOptions>)))
        {
            services.TryAddSingleton<ExecutionAdmissionCoordinator>();
        }

        // The executor is drained by the worker's JobExecutionService. Producers/fences resolve as
        // an IEnumerable so an executor with no registered producer for a plan fails that job
        // cleanly rather than blocking worker startup.
        services.AddSingleton<IJobExecutor, TileExportJobExecutor>();

        return services;
    }
}
