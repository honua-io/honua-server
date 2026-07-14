// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Registers the durable shadow-topology rebuild worker (#2718) and its self-healing
/// reconciler (#2720). Self-gates on the routing rebuild store and the shared execution-job
/// store both being registered, mirroring <c>ProvisionerServiceCollectionExtensions</c>: on
/// a non-Postgres data provider or a Redis-less profile, this is a no-op rather than an
/// unconditional worker that can never claim anything.
/// </summary>
internal static class NetworkTopologyRebuildServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="NetworkTopologyRebuildJobExecutor"/> worker, the
    /// <see cref="NetworkTopologyRebuildSubmissionService"/> submission surface, and the
    /// <see cref="NetworkTopologyRebuildReconciler"/> self-healing background service.
    /// </summary>
    public static IServiceCollection AddNetworkTopologyRebuildJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(INetworkTopologyRebuildStore)))
        {
            // Non-Postgres data provider: the rebuild store (and every other network-topology
            // store) is not registered, so there is nothing this worker could ever claim.
            return services;
        }

        services.AddSingleton<IJobExecutor, NetworkTopologyRebuildJobExecutor>();
        services.TryAddScoped<NetworkTopologyRebuildSubmissionService>();
        services.TryAddScoped<NetworkTopologyRebuildReconciler>();

        if (services.Any(d => d.ServiceType == typeof(IExecutionJobStore)))
        {
            // The reconciler needs the durable job store to distinguish an orphaned attempt
            // (owning job already terminal) from one merely awaiting worker takeover. Without
            // AddJobOrchestration having run first (Redis-gated), there is no job store to
            // reconcile against, so the background service would have nothing to do.
            services.AddHostedService<NetworkTopologyRebuildReconcilerBackgroundService>();
        }

        return services;
    }
}
