// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Tiles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the durable tile-export runtime: the shared, protocol-neutral lifecycle service and
/// the worker-side job executor. Concrete source producers and fences are contributed by each
/// protocol adapter (MapServer, ImageServer) through their own registrations and flow into the
/// executor via the injected <c>IEnumerable</c> seams.
/// </summary>
public static class TileExportRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITileExportJobService"/> and the <see cref="IJobExecutor"/> for
    /// <c>ExecutionJobKind.TileExport</c>. Optional execution-substrate collaborators (store, queue,
    /// admission) are resolved leniently so the service degrades cleanly when durable jobs are not
    /// configured, matching the geoprocessing lifecycle wiring.
    /// </summary>
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
            sp.GetService<IExecutionAdmissionEvaluator>()));

        // The executor is drained by the worker's JobExecutionService. Producers/fences resolve as
        // an IEnumerable so an executor with no registered producer for a plan fails that job
        // cleanly rather than blocking worker startup.
        services.AddSingleton<IJobExecutor, TileExportJobExecutor>();

        return services;
    }
}
